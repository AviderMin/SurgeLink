## 结论

> 2026-10-24 实施更新：已开始 CLI 优先路线，新增共享 Gateway/Client、当前用户命名管道、
> 桌面按连接授权、CLI 与共用客户端的 MCP stdio Host。现已增加顶栏机器人统一入口、
> 当前可见 PTY 的文本/按键输入、增量输出、人工接管、输入去重及活动记录。
> 执行策略由外部 Agent 应用负责；使用方法与当前边界见 [Agent 接入文档](AGENT-CLI.md)。
> 以下是实施前的评审记录，其中“当前 HEAD 无实现”、分支与测试产物描述属于历史状态；
> 逐客户端配对、独立 exec 结果与文件修改专用 API 仍是后续工作；终端控制可执行远端命令。

SurgeLink 作为远程终端工作台的底座是合格的：协议插件化、终端引擎独立、UI 与协议解耦、离线测试覆盖也比较完整。

但**当前 HEAD 还不具备外部 Agent 接入条件**。`agent-use` 与 `master` 指向同一个提交，Agent 目前只有设计文档，没有可构建的实现。现阶段最该做的不是先写 MCP Server，而是把**会话授权、命令执行、终端观察、文件变更**这四个边界内核化，否则 MCP 层只能直接操作 UI 状态和协议对象，后续很难保证安全与稳定。

---

## 一、项目审查

### 1. 值得保留的基础

- **协议能力模型清晰**：`IProtocolSession` 通过可空的 `Terminal` / `FileSystem` / `Monitor` 表达能力，UI 按 `SessionCapabilities` 构建视图，新增协议不需要改壳层。
- **终端引擎边界干净**：`SurgeLink.Terminal` 不依赖 Core 和 UI，字节进、单元格网格出，适合做 Agent 的屏幕快照基础。
- **测试基础较好**：现有 Release 测试产物共 945 项通过（Terminal 285、Core 230、SSH 324、Conformance 58、Telnet 16、Serial 32），协议契约测试也已成体系。
- **设计文档方向正确**：`docs/AI-AGENT-MCP-DEVELOPMENT.md` 已经明确“外部规划、本地裁决”“会话范围明确”“默认最小暴露”“操作可见可停止”等原则，这些应保留。

### 2. Agent-ready 的关键缺口

| 需要的能力 | 当前实现 | 直接接 Agent 的后果 |
|---|---|---|
| 授权会话模型 | 只有进程内 `SessionId`，没有对外句柄、连接代次、授权范围 | 无法可靠判断“这个请求属于哪次连接”；重连、关闭标签后旧请求可能仍指向旧对象 |
| 独立命令执行 | `SshCommandRunner` 是 SSH 内部类，`Result`/`Error` 全量收集 | 无输出上限，可能 OOM；无明确的超时/取消/`outcome_unknown` 语义 |
| 终端观察与输入 | `GetVisibleText()` 只给视口；`TerminalBridge.Send` 是 `async void` | 不能读取有界输出日志；Agent 无法知道写入是否成功；人工输入与 Agent 输入会交错 |
| 文件变更 | `RemoteFileWrite` 先 `ListAsync` 再提交，只有 `overwrite` 布尔值 | 典型的 check-then-commit 竞态；没有基线散列，Agent 可能覆盖外部修改 |
| 工作区快照 | `WorkspaceSnapshot` 内含可变的 `WorkspaceTab` | 不是真正的跨线程不可变快照，Agent 适配器容易读到底层 UI 状态 |
| 授权、审批、审计、IPC | 当前均无 | MCP 层没有可依赖的信任边界 |

其中几处具体代码需要特别注意：

- `src/SurgeLink.Protocols.Ssh/SshSession.cs:94-122`：`Terminal` / `FileSystem` / `Monitor` 在 `lock` 外先检查 `IsDisposed`。并发调用时，可能在 `DisposeAsync` 之后创建新的 `SshFileSystem`，而后者还能通过 `SshConnectionFactory` 新建 SFTP 连接。
- `src/SurgeLink.Protocols.Ssh/Internal/SshCommandRunner.cs:27-39`：`sshCommand.Result` / `Error` 会完整缓冲 stdout/stderr，没有字节预算，不能作为 Agent 执行入口。
- `src/SurgeLink.App/Sessions/TerminalBridge.cs:62-117`：`SendText` / `SendPaste` 最终进入 `async void Send`，异常只写 debug 日志；Agent 无法等待写入完成。
- `src/SurgeLink.App/Sessions/WorkspaceStore.cs:244-269`：关闭标签时先释放传输队列、终端和会话，最后才从快照移除 tab；没有“SessionClosing”事件让 Agent 租约先失效。
- `src/SurgeLink.Core/Files/RemoteFileWrite.cs:18-35`：目录列表检查和最终提交之间没有版本条件；`overwrite: true` 会无条件覆盖。
- `src/SurgeLink.Terminal/ITerminalEmulator.cs`：只有 `GetVisibleText` 和选区文本，没有按范围读取 scrollback 或输出日志的接口。

### 3. 另外几个基线问题

这些问题不全是 Agent 引入的，但 Agent 会放大它们：

- `src/SurgeLink.Protocols.Ssh/ScpFileSystem.cs:273-280`：SCP 回退的 `RenameAsync` 使用普通 `mv`，目标已存在时可能覆盖，目标为目录时还可能把源移动进目录。
- `src/SurgeLink.Core/Storage/JsonSessionStore.cs`：存储只在构造时读盘，后续整文件写回；两个应用实例同时保存会丢数据。`AtomicJsonFile` 仍使用固定 `.tmp` 文件名，也不是跨进程锁。
- `src/SurgeLink.App/Views/Sftp/FileEditorWindow.cs:209-223`：编辑器保存始终 `overwrite: true`，没有基线散列，外部修改会被覆盖。Agent 文件编辑不能复用这条路径。
- `src/SurgeLink.App/Services/InteractionService.cs:148-157`：排队中的交互请求没有注册取消；取消 token 后仍要等到队首才会完成。
- `src/SurgeLink.App/Sessions/WorkspaceStore.cs` 的 `WorkspaceSnapshot` 不是真正不可变，`WorkspaceTab.State/View/Message` 都是可变字段。

---

## 二、建议的接入形态

首期建议采用：

```text
外部 Agent / MCP Client
        │  MCP stdio
SurgeLink.AgentHost（只做协议适配，不引用 SSH.NET、不碰 UI）
        │  当前用户命名管道，长度前缀 + contractVersion
SurgeLink 桌面进程
  Agent Gateway
    ├─ 授权 / 会话句柄 / 连接代次
    ├─ 策略、审批、操作状态、审计
    └─ IAgentWorkspaceAdapter
          └─ SessionLease
                └─ 现有 IProtocolSession
```

为什么先选命名管道，而不是本地 HTTP：

- 不开放 TCP 监听端口，不需要处理 Origin、Bearer token、CSRF、DNS rebinding。
- SSH 凭据、主机密钥、审批 UI 始终留在桌面进程。
- `AgentHost` 可以随 MCP 客户端启动和退出，MCP SDK 的故障不会影响桌面 UI。
- 以后要加 CLI 或 HTTP，只需在同一套 Gateway 上增加适配器，不必重写权限内核。

不要反过来做：不要让 `AgentHost` 直接加载 `SurgeLink.Protocols.Ssh`，也不要让 MCP 层直接调用 `WorkspaceStore`。

---

## 三、开工前应冻结的五个契约

### 1. `IAgentWorkspaceAdapter`

只暴露不可变 DTO：

```csharp
public interface IAgentWorkspaceAdapter
{
    ValueTask<AgentSessionDescriptor[]> ListSessionsAsync(CancellationToken ct);
    ValueTask<IAgentSessionLease> AcquireAsync(
        string sessionHandle,
        AgentScope scope,
        CancellationToken ct);
}
```

`AgentSessionDescriptor` 只包含句柄、脱敏标签、协议、能力和限制，不包含 `WorkspaceTab`、`IProtocolSession` 或 WinUI 对象。

### 2. `AgentSessionLease`

租约至少绑定：

- `sessionHandle`：随机值，不是保存配置 ID，也不是 `SessionId`
- `sessionId`
- `connectionGeneration`：每次新建或重连生成新值
- `bridgeId`：调用者连接身份
- 授权范围、目录 roots、预算、到期时间、策略版本

`WorkspaceStore` 需要增加 `SessionClosing` 事件，并在关闭标签、重连、应用退出时**先撤销句柄和取消操作，再释放协议资源**。旧租约立即失效。

### 3. `IRemoteCommandExecutor`

这是当前最大的 Core 缺口：

```csharp
public interface IRemoteCommandExecutor
{
    Task<RemoteCommandResult> ExecuteAsync(
        RemoteCommandRequest request,
        IProgress<RemoteCommandChunk>? progress,
        CancellationToken cancellationToken);
}
```

请求中必须有 `MaxOutputBytes`、`Timeout`、`ShellKind`、`WorkingDirectory`；结果中必须有：

- `int? ExitCode`：缺失时不能猜测为 0
- `Truncated`
- `StopReason`：`exited` / `timeout` / `cancelled` / `output_limit_exceeded` / `channel_failure`
- `OutcomeKnown`：无法确认远端是否终止时返回 `outcome_unknown`

输出必须在**接收时**限流，不能先让 SSH.NET 收集完整 `Result` 再裁剪。SSH 的 exec 通道要和用户交互终端分开。

### 4. `ITerminalObservation` + `IControlledTerminalInput`

- `ITerminalObservation`：在 `Emulator.SyncRoot` 下复制当前屏幕；另外建立有界输出环形缓冲区，带序号和游标。首期只开放 `source=screen`，不要假装屏幕快照能证明命令完成。
- `IControlledTerminalInput`：返回可等待的写入结果，维护 `InputRevision`；用户一旦手动输入，立即撤销 Agent 的输入租约；alternate screen、鼠标模式、密码提示等场景默认拒绝。

### 5. `IFileMutation` / 写入协调器

文件修改必须支持：

- `expectedSha256` 基线
- 同目录暂存
- 提交前再次核对基线
- 提交后复读验证
- 与 UI 编辑器、传输队列共享写锁

但要说清楚：普通 SFTP/SCP **没有服务端 compare-and-swap**。上述措施只能降低竞争窗口，不能保证绝不丢更新。若要求严格保证，必须使用远端受限账户、专用子系统或服务端条件提交，否则应拒绝自动覆盖。

---

## 四、实施顺序

### P0：契约与安全骨架

- 冻结 `contractVersion`、会话句柄、连接代次、操作状态、错误码。
- 建立 `IAgentWorkspaceAdapter`、`AgentSessionLease`、`IAgentAuditSink`。
- 用假协议会话做单元测试，不接 UI、不接真实 SSH。
- 清理当前仓库中的 Agent 原型残留构建产物，避免旧 `obj` 继续污染构建。
- 文档拆成三份：产品/架构决策、威胁模型、v1 工具与错误契约。

**门槛**：未配对无法读取任何会话；跨 bridge 复用句柄失败；关闭标签后旧租约立即失效。

### P1：只读闭环

首批工具控制在 6 个左右：

- `list_sessions`
- `get_session_info`
- `read_terminal`（仅 `screen`）
- `list_directory`
- `read_text_file`
- `get_system_snapshot`
- 可选 `list_processes`

同时完成：

- `AgentHost` stdio + 命名管道
- 设置页的“外部 Agent”总开关、配对卡片、授权范围
- Agent 操作面板
- 基本审计

**这一阶段不做**：命令执行、文件修改、终端输入、传输。

### P2：受控命令执行

- `run_command` + 单次审批
- 操作 ID、状态查询、取消、去重键
- 命令参数散列绑定审批
- 输出预算、超时、`outcome_unknown`
- 审批拒绝后零远端 I/O
- Agent 不能调用 `approve_operation`

### P3：文件修改、传输、终端输入

按风险从低到高：

1. `propose_text_edit`
2. 单文件上传/下载，使用桌面文件选择器生成的本地句柄
3. `send_terminal_input`，必须有人工接管与输入租约

### P4：CLI、HTTP、兼容性

- 先做 CLI（便于调试、脚本化和审计）
- 有明确宿主需求后再做 loopback HTTP/WebSocket
- 至少两类真实 MCP 客户端互操作
- x64 / ARM64 安装包包含 `AgentHost`
- 真实 SSH 集成测试单独跑，不依赖模型

---

## 五、几个明确不建议的做法

- **不要**把 `WorkspaceStore`、`TerminalBridge` 或 `IProtocolSession` 直接暴露给 MCP 层。
- **不要**在 v1 提供 `invoke_any_method`、任意本地文件路径、任意连接创建。
- **不要**按命令前缀自动放行原始 shell 命令。`cat /etc/shadow` 也是“只读”；命令分类器不能替代路径授权。
- **不要**把 MCP 工具标注的 `readOnlyHint` 当作安全边界，最终裁决必须在桌面 Gateway。
- **不要**信任 `clientInfo.name` 或工具参数里的 `clientId`；调用者身份只能来自实际 IPC 连接和配对记录。
- **不要**把“原子替换”宣传成“不会覆盖别人的修改”；两者不是一回事。
- **不要**在 v1 开 TCP 监听端口；HTTP 的认证、Origin、重放和 TLS 应单独评审。
- **不要**在应用重启后自动恢复审批或重跑修改命令；未完成操作只能标为中断或结果不确定。

---

## 六、本地工作区的额外发现

当前本地 Git 里还留有一次未合入的 Agent 原型痕迹：

- `git reflog` 中有 `mcp-beta` 的提交 `1e1a489`，包含较完整的 Agent 项目、传输层、审批和 91 项 Agent 测试通过记录；当前 `agent-use` 分支并不包含它。
- `f7a8a5f` 是一个 stash 提交，引用了 `SurgeLink.Agent.Client` 和 `SurgeLink.Cli`，但树中不存在这些项目，属于半迁移状态，不能直接构建。
- `src/SurgeLink.Agent*` 只剩 `bin/obj`，`src/SurgeLink.App/obj/project.assets.json` 仍指向已删除的 Agent 项目；这会让 `--no-restore` 构建出现过期警告。
- 工作树还有一个未跟踪的 `%SystemDrive%/` 目录，是脚本变量未展开产生的残留，建议清理。

这个原型可以当作参考实现，但不建议整包合并。里面有几处需要先修的点：

- `CommandPolicy` 对“只读命令”的自动放行可以让 `cat`、`grep`、`find` 读取授权目录之外的远端文件；文件 roots 对命令不生效。
- `ClientIdentity` 把客户端 secret 以普通 JSON 写入用户目录，和文档宣称的 DPAPI 保护不一致。
- `AgentAuthorization` 的 pairing 只存在内存中，桌面重启后并不能真正恢复授权。
- 命名管道、审批、操作状态这些设计值得保留，但应先与当前 HEAD 的契约重新对齐。

现有 Release 测试二进制 945 项通过；当前沙箱环境下 `dotnet restore` 受 NuGet 特殊目录解析影响未能完成，因此这不能算一次干净源码构建的证明。`dotnet build App --no-restore` 虽成功，但出现了上述过期 `Agent.Contracts` 引用警告，正好说明需要先清理旧原型残留。

**如果只做一件事**：先实现 `AgentSessionLease`、`IRemoteCommandExecutor` 和 `IAgentWorkspaceAdapter` 三个契约及其假实现测试。它们决定了后面的 MCP、CLI、HTTP 是否能成为薄适配器，而不是又一套绕过 UI 的远程控制路径。
