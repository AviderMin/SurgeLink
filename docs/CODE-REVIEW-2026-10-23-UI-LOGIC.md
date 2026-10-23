# 代码逻辑与 UI 一致性审查

日期：2026-10-23。范围：`src/` 全部五个项目（App / Core / Terminal / Protocols.Ssh / Protocols.Demo）的静态审查，重点为 WinUI 3 + Reactor 设计一致性、连接与会话生命周期逻辑、稳定性。本次审查未修改任何源码；以下为发现与建议。

## 验证基线（本次实测）

- `dotnet test SurgeLink.slnx -c Release -p:Platform=x64`：844 项通过、0 失败、0 跳过（Terminal 285、Core 198、SSH 321、Conformance 40），与 `docs/CODE-REVIEW-2026-10-23.md` 记录一致。
- 工作区 git 状态干净；审查基于提交 `ae4d4f5`。
- 未执行真实 SSH 服务器、桌面 UI 端到端、安装包与 Windows 10 / ARM64 实机验证；这些边界与既往审查一致。

## 一、UI 设计一致性（对照 docs/UI-GUIDELINES.md）

结论：整体一致性强，已达到指南要求，以下为逐条核查结果。

| 指南条目 | 结论 | 证据 |
|---|---|---|
| 无 XAML / 无 MVVM，纯 Reactor 组件 | 通过 | App 层无 .xaml 文件；视图均为 `Component`/`Component<TProps>`，props 为不可变 `record`。 |
| Hook 无条件、定序调用 | 通过 | 全量扫描 `UseState/UseEffect/UseMemo/UseExternalStore/UseRef` 未发现 `if/for/foreach` 分支内的条件调用；`MonitorView.cs:30` 等关键文件有注释明确约束。 |
| 动态列表稳定 key | 通过 | 动态列表（SFTP 行、监控行、标签页）均带 `.WithKey(...)`；扫描到的 19 处无 key 的 `Select` 均为静态选项（ComboBox 条目、Grid 行定义、字符串 join），无需 key。 |
| 状态集中在 Store + UseExternalStore | 通过 | `MainShell.cs:47-52`、`MonitorView.cs:31`、`SidebarMonitor.cs:37` 均经 `UseExternalStore(store.Subscribe, () => store.Snapshot)` 读取；`WorkspaceStore.Update` 用 Revision 解决就地突变与 record 值比较冲突（`WorkspaceStore.cs:445-466`），是正确的 Reactor 集成模式。 |
| 文件 ~300 行拆分 | 通过 | 最大视图 `SftpView.cs` 698 行但已按 `SftpContent/SftpToolbar/SftpEntryList/SftpDialogs/SftpOperations/SftpTransferPanel` 拆分职责；终端绘制拆为 8 个 partial 文件。 |
| 4px 网格 / 间距 token | 通过 | 全部间距使用 `AppTheme.Spacing*`；扫描到仅 3 处非 token 的 Padding/Margin 字面量（`AppDialog.cs:34` 16/24、`FileEditorWindow.cs:336` 8），全部在 4px 网格上，属合法微调。 |
| 圆角规范（卡片 8 / 控件 4 / 窗口归属 DWM） | 通过 | `AppTheme.CardRadius=8`、`ControlRadius=4`；未对窗口根布局加圆角。 |
| 颜色仅 Theme.* token | 通过 | 全 App 层扫描 0 处十六进制颜色、0 处 `Color.FromArgb`（唯一例外是 `TerminalStyleResolver.cs:85` 解析用户终端调色板——指南明确豁免终端调色板）。状态色经 `AppTheme.StatusBrush` 走 HC 安全 token。 |
| 字级使用 type ramp | 通过 | 标题/正文用 `BodyStrong/Caption/Title` 等 ramp 工厂；11 处 `.FontSize(12/13/14)` 均为等宽辅助文本（行号、PID、路径），属表格/终端数据呈现，可接受。 |
| 本地化 | 通过 | 409 处 `Str.T` + 51 处 `Str.F`，0 处裸英文字面量传入文本控件，0 处 `string.Format` 字面量；中文副本集中于 `Localization/Zh/`。 |
| 可访问性 | 通过 | 80 处 `AutomationName`、29 处 `AutomationId`（壳层区域、编辑器协议框、SidebarMonitor 等）；图标按钮均有文字 AutomationName（如 `FileEditorWindow.cs:293`）。 |
| 主题跟随用户设置 | 通过 | `ThemeController.Apply` 作用于壳根；独立编辑器窗口额外对 `XamlRoot.Content` 应用主题（`FileEditorWindow.cs:473-483`），处理了多窗口场景。 |
| 失败以 InfoBar 呈现、可重试 | 通过 | `MonitorView.cs:159-172`（含 Retry 动作）、`SftpView` banner、编辑器错误条均为 InfoBar；未用阻塞对话框报操作失败。 |
| 空状态指引 | 通过 | 监控无会话/无能力占位（`SidebarMonitor.BuildPlaceholder`）、SFTP 空目录、终端未连接态均有解释文本。 |
| 卸载时取消异步工作 | 通过 | 所有 `UseEffect` 返回 `() => cancellation.Cancel()`（MonitorView、SidebarMonitor、FileEditorWindow 等）；`FileEditorWindow` 的 `lifetime` CTS 按 `FileSystem+FullPath` memo 并在卸载时取消+释放（`FileEditorWindow.cs:49,165-167`）。 |
| 大文件保护 | 通过 | 编辑器 4 MB 上限前后端双重检查（`FileEditorWindow.cs:30,179,221`；`BoundedTextFile` 按字节截断）。 |

### UI 层小改进（非阻塞，可选）

1. `StatusBarView.cs:34`、`MonitorProcessTable.cs:129-149`、`SftpDialogs.cs` 等 11 处 `.FontSize(12)` 可考虑集中到 `AppTheme`（如 `DataFontSize = 12`），便于日后统一调整。属风格建议，非缺陷。
2. `MonitorProcessTable.cs:109-112` 进程表按行构建 Grid 且行数上限 15（`MonitorView.cs:73` 取 top 15），规模受控，无虚拟化需求。

## 二、代码逻辑审查

上一轮（CODE-REVIEW-2026-10-23.md）四项 P1/P2 均已修复并核实：

- **重连旧配置覆盖** → `WorkspaceStore.cs:195-207` 在锁内仅合并"仍存在且连接参数匹配"的记录；`GetReconnectProfile` 优先读最新保存值。
- **重连绕过凭据询问** → `MainShell.ConnectAsync` 统一入口，重连也走 `BuildConnectOptionsAsync`，且关闭旧标签前先取得凭据（`MainShell.cs:129-143`）。
- **文本编码丢失** → `BoundedTextFile.ReadDocumentAsync` 保留 UTF-8/16/32 LE/BE 及 BOM 策略，`TextFileDocument.Encode` 原样回写。
- **终端查询无响应通道** → `TerminalBridge.cs:28,111` 订阅 `Emulator.ResponseRequested` 并回写远程通道，释放时退订。

本轮新核查的关键逻辑均正确：

- **SessionManager 并发**：`OpenAsync` 的"连接成功后未被接受则释放"路径（`SessionManager.cs:76-92`）处理了 dispose/取消竞争；`CloseCoreAsync` 先在锁内摘除再释放，无重复释放；事件回调全部包 try/catch。
- **SshSession 状态机**：能力句柄双重检查锁定 + dispose 后返回 null；`LoadHostInfoAsync` 缓存任务、取消时清空缓存允许重试（`SshSession.cs:153-164,262-270`）；传输错误仅在确实断连时置 Faulted。
- **FileTransferQueue**：入队与 worker 启动在同一锁内（`FileTransferQueue.cs:74-91`），无"队列空检查与退出之间漏任务"竞态；`DisposeAsync` 有 10s 有界等待与超时日志；下载写临时文件 + `File.Move(overwrite:false)` 拒绝并发覆盖；ZIP 下载拒绝符号链接/特殊文件并校验重复归档路径。
- **MonitorView/SidebarMonitor 采样循环**：取消令牌在 effect 清理时触发；`SampleAsync` 与 `Task.Delay` 均观察令牌；异常仅写错误状态不逃逸 Task.Run；`GetTopProcessesAsync` 失败降级为空列表不中断主采样。
- **凭据解析**：`SessionManager.BuildRequest` 仅在 options 缺字段时才读持久化凭据（`SessionManager.cs:157-159`），一次性输入不会被存储值意外覆盖；公钥缺文件报 Configuration 类 ProtocolException，属用户可读错误。
- **键盘交互认证**：`SshConnectionFactory.AnswerPrompts` 有 16 轮上限，提示任务经 `TaskObservation.ObserveFaults` 防未观察异常，取消时回退密码。
- **崩溃诊断**：`App.cs` 挂 `AppDomain.UnhandledException` 与 `TaskScheduler.UnobservedTaskException`（后者 SetObserved 防进程终止），`MainShell` 挂 XAML `UnhandledException`，CrashLog 落盘失败静默——三层覆盖完整。

### 逻辑层建议（均为低优先级加固，非缺陷）

1. **`AtomicJsonFile.Read` 的 catch 集合不含 `NotSupportedException`/`SecurityException`**（`AtomicJsonFile.cs:28`）：异常路径仍返回 fallback 的契约在罕见文件系统错误（如长路径策略）下不成立。建议补 `NotSupportedException`。
2. **`WorkspaceStore.OnTerminalClosed` 直接写 `tab.Message` 并强制 Update**（`WorkspaceStore.cs:439-443`）：TerminalBridge 保证 Closed 在 UI 线程派发（`TerminalBridge.cs:141`），当前安全；若未来协议在后台线程触发，需先 `_dispatcher.Post`——建议在方法注释中固化该前提。
3. **`CrashLog.Write` 的 catch 不含 `NotSupportedException`**，同第 1 点，可一并加固。
4. `SidebarMonitor.cs:56` 每次 effect 重建 `MonitorHistory`：切换间隔会丢失滚动窗口历史。若希望换采样间隔后保留历史，可像 `MonitorView` 一样把 history 移到 ConditionalWeakTable。当前行为（重新采样）可接受且语义清晰。

## 三、稳定性评估

- **线程模型**：UI 线程唯一写 UI 状态；协议回调经 `IUiDispatcher.Post`；终端模拟器经 `SyncRoot` 串行（bridge 唯一写、renderer 唯一读），与 ARCHITECTURE.md 描述一致，实测无违例。
- **资源释放**：会话 → terminal/filesystem/monitor 子能力 → SSH client 的释放链完整且幂等；传输队列先于协议会话销毁，避免"用死连接传输"；编辑器窗口随会话断开自动置失效并提示保留未保存内容。
- **重入保护**：连接并发由 `connectPending` + `Snapshot.IsConnecting` 双重防护；模态由 `shellModal` + `QueuedDialog.IsBusy` 防护；SFTP 粘贴、编辑器保存均有 busy 标志。
- **测试覆盖**：844 项离线测试全绿；UI 自动化挂钩（AutomationId）齐备，`artifacts/final-review` 有审查 harness。

## 结论

代码逻辑、WinUI 3 + Reactor 设计一致性、稳定性三项均达标，未发现新的 P0/P1/P2 缺陷。上方 4 项低优先级建议（3 项异常集合加固、1 项历史保留策略）可作为后续小改进，不阻塞当前版本。
