# 代码审查与修复报告 — 2026-10-27

分支 `agent-use`，基线提交 `e61558c`。本报告记录本轮团队协作审查（5 个并行审查线程，
按目录划分配写范围）发现并修复的缺陷、明确排除的误报，以及验证结果。
所有改动均未提交，工作区即为最终状态。

基线情况：解决方案 0 警告 0 错误全部通过，但基线全量测试中有 1 项偶发失败
（见 §4），说明"基线绿"本身包含一个不稳定的性能断言。

## 1. 已修复的真实缺陷

### 安全 / 隐私
| 级别 | 位置 | 问题 | 修复 |
|---|---|---|---|
| 高 | `src/SurgeLink.App/Services/PrivacyDisplay.cs:56` | 隐私模式只遮蔽了文字，悬停提示（ToolTip）仍给出原始主机名；且 `.Private()` 和悬停还原逻辑会给每个标签挂上未遮蔽的提示。侧边栏、状态栏、会话标签一悬停就泄露 | 提示文本也走 `Mask`，标签自身的悬停还原保持不变；同时修正 Shell 中显式 `.ToolTip(...)` 的若干调用 |
| 中 | `src/SurgeLink.App/Views/Sftp/SftpView.cs` + `SftpListInput.cs` | 列表喂入的是"过滤隐藏文件"后的行，但双击/快捷键/多选全部按位置索引进未过滤的 `entries`：显示隐藏文件关闭时，排序第一的条目被隐藏就会导致**对另一个远程文件**执行打开/改名/删除/下载 | 列表只喂可见行，选择与快捷键按 `RemoteFileEntry` 实例解析（`SelectedItem`/`SelectedItems`/`ItemFromContainer`），重建选择按 FullPath 比对 |
| 中 | `src/SurgeLink.Protocols.Ssh/Internal/SshCommandRunner.cs` | `StreamReader(detectEncodingFromByteOrderMarks: true)` 会删除输出中**任意位置**的 `EF BB BF` 字节序列：U+FEFF 被吞、Latin-1 文本被破坏；Windows 监视脚本开头的 UTF-8 BOM 导致 `@@@SURGELINK:cpu` 标记失配，**Windows 采样中的 CPU 段被静默丢弃** | 直接按编码解码，仅在支持签名的编码中剥离开头一个 U+FEFF |
| 中 | `src/SurgeLink.Agent/AgentGateway.cs:40-53` | `GrantSession` 只比较连接键就复用旧句柄：桌面重连后新会话会继承**旧会话**的授权，`sessions.check` 回报旧主机/旧登录，破坏强制目标核对 | 只有同一会话对象且同一终端才复用句柄，否则要求先撤销 |
| 低 | `src/SurgeLink.App/Shell/SettingsDialog.cs:132,144` | 下拉框回调无边界检查，越界即在 WinUI 回调中抛异常终止进程 | 越界时保留原值 |

### 正确性 / 数据
| 级别 | 位置 | 问题 | 修复 |
|---|---|---|---|
| 中 | `src/SurgeLink.Protocols.Ssh/Monitoring/CpuUsageCalculator.cs` | /proc/stat 的 iowait 可能倒退，负增量使空闲总量低于忙总量，空闲主机被报成 100% CPU | 各字段先取 0 下限再求和 |
| 中 | `src/SurgeLink.Core/Sessions/SessionManager.cs:171` | 只要任一 `ConnectOptions` 密钥缺失就去解密凭据库，输入密码也会解密根本用不到的记忆私钥口令 | 只在本次真正需要（密码认证缺密码 / 公钥认证缺口令）时读取 |
| 中 | `src/SurgeLink.Core/Monitoring/RemoteMonitorSampler.cs:114-125` | 采样线程退出时无条件重启，最后一个订阅退订后新线程的取消源已不可达：关闭监视视图后仍按间隔轮询远端直到会话关闭 | 只有该线程持有共享取消源且仍有订阅时才重启 |
| 中 | `src/SurgeLink.App/Views/Sftp/FileEditorWindow.cs` | 加载无世代号：初次读取未完成时"重新加载 → 放弃 → 重新加载"，旧读取可能最后落地并覆盖新文本，静默丢失编辑 | 增加加载世代，只有最新一次加载可以发布结果 |
| 低-中 | `src/SurgeLink.Core/Files/LineLayout.cs` | 只把 `\n` 当换行，CRLF 文档的行号槽每行多标一行、`LineAt` 行号错误 | CRLF 视为一次换行，孤立 `\r` 也计一次 |
| 低 | `src/SurgeLink.App/Shell/SettingsDialog.cs:54` | 语言下拉按菜单顺序取值，枚举顺序不同（System/English/Chinese），选英语存成中文，重启后互换 | 通过 `LanguageOrder` 表映射 |
| 低 | `src/SurgeLink.Terminal/ScreenBuffer.cs:211,229` | 制表位可能落在宽字符的右半格，后续字符与 `ClearWidePair` 会吃掉 `中` 的一半 | 跳格时跳过 continuation 格 |
| 低 | `src/SurgeLink.Terminal/TerminalMouseEncoder.cs` | 经典（非 SGR）鼠标协议把横向滚轮编码成 66/67，而这两个码是"按键 3 按下/松开"，应用侧收到幽灵点击 | 明确 64/65/66/67 语义，经典协议拒绝横向滚轮 |
| 低 | `src/SurgeLink.App/Views/Terminal/TerminalSurface.Interaction.cs:219` | 指针坐标少减了滚动条列宽（处理器挂在根 Grid 上，含 16 DIP 滚动条列），点击定位/选择整体右偏约一列 | 减去 `ScrollBarWidth`（仅静态核对，见 §5） |
| 低 | `src/SurgeLink.App/Views/Terminal/TerminalSurface.Paint.cs:44` | `ViewportSize = emulator.Rows` 把可拖拽范围限制为 `Maximum - ViewportSize`，最老的滚动历史不可达 | `ViewportSize = 0`（WinUI RangeBase 只会用 Min/Max 夹取 Value，已核对文档与实现） |
| 低 | `src/SurgeLink.App/Views/Sftp/SftpOperations.cs:61` | "新建文件"在目标已存在时要先上传空载荷才失败 | 先列目录给出明确冲突信息（仍以写入端 fail-closed 为准） |
| 低 | `src/SurgeLink.App/Views/Sftp/SftpContent.cs:44`、`FileEditorWindow.cs:410`、`MonitorView.cs:127` | `ProgressRing()` 未设置 `IsActive`，加载态显示一个静止的圆环 | `IsActive(true)` |
| 低 | `src/SurgeLink.App/Views/Monitor/MonitorView.cs:210` | 暂停时"立即采样"仍可点击 | `.IsEnabled(!paused)` |
| 低 | `src/SurgeLink.App/Shell/SessionEditorDialog.cs:271` | 设备"刷新"按钮被塞进 104px 定宽列，中文标签溢出 | 该列改为 `Auto` |
| 低 | `src/SurgeLink.Core/Security/JsonHostKeyStore.cs:39` | `Save(null)` 读盘后解引用空参数抛 NRE | 入口 `ArgumentNullException.ThrowIfNull` |
| 低 | `src/SurgeLink.Protocols.Ssh/SshSession.cs:448` | 远端标识串（主机名、os-release）直接进 UI 与日志，含控制字符可伪造日志行 | `SanitizeIdentity` 折叠控制符并限长 120 |
| 低 | `src/SurgeLink.Protocols.Ssh/Internal/ScpMetadata.cs` | 部分 BusyBox 不支持 `readlink -n`，诊断信息进 stdout 会截断记录 | 每个 shell 探测一次并回退 |

### 构建 / 发布工具
| 位置 | 问题 | 修复 |
|---|---|---|
| `tools/package-appx.ps1`、`tools/package-exe.ps1` | 发布的载荷架构从未校验，ARM64 安装包可能装进 x64 二进制 | 打包前读取 PE Machine 字段，与 `-Architecture` 不符即失败 |
| `tools/verify-app.ps1` | 应用有多个顶层窗口（Shell + 独立文本编辑器），`FindFirst(ProcessId)` 会返回编辑器窗口，之后的 SFTP 断言全部落在错误窗口上；另有两条断言与 2026-10 的文件夹下载/多选删除特性相矛盾 | 按窗口名选 Shell；把两条断言改成当前契约（文件夹可递归下载、多选可一次删除） |

## 2. 测试稳定性
`SurgeLink.Terminal.Tests` 的 `PerformanceTests.OneMegabyteOfMixedAnsiIsFedQuickly` 是**结构性不稳**：
单次 64 KB 预热后只取一次墙钟样本（实测 101–157 ms，占 200 ms 预算的 50–78%），且与约 30 个
测试类并行。改为预热后取 3 次最好成绩，并把整类移入 `DisableParallelization` 集合；预算仍是 200 ms。
15 次连跑（含 5 次在 6 个忙循环进程下）全部通过。

## 3. 明确排除的"缺陷"（已验证为误报）
- `SshSession` 能力属性在锁外判定 `IsDisposed`：外层只是快速路径，每个 getter 在同锁内二次校验，已有回归测试覆盖。
- `AgentGateway` 未列出的方法落入 `files.read`：`IsKnownMethod` 先行拒绝（新增测试钉住）。
- `CliArguments` 的 `||`/`&&` 优先级：`--submit` 不会进入选项分支（新增负例）。
- `AtomicJsonFile` 的临时文件名与 flush、`LocalDownloadPath` 的根前缀判断、传输队列的临时文件发布、`RemoteFileWrite` 的暂存权限顺序：均正确。
- `McpAdapter` 参数校验、`AgentPipeServer` 连接槽位、`ScpFileSystem` 的 rename 防覆盖：均正确。
- 隐私：会话编辑器的主机字段本来就是 PasswordBox 遮蔽、`ApplyGroup` 在 Loaded 时刷新；未改。

## 4. 验证
- `dotnet build SurgeLink.slnx -c Debug` → **0 警告 0 错误**
- `dotnet test SurgeLink.slnx -c Debug` → **1281 通过 / 0 失败**（基线 1255；Terminal 324、SSH 402、Core 344、Agent 91、Conformance 72、Telnet 16、Serial 32）
- `python tools/check-repository.py` → 453 文件 0 发现；`python tools/check-ui-localization.py` → 584 处 0 发现
- `python -m unittest tools/tests/test_release_notes.py`、`tools/tests/test-release-tag.ps1` → 全部 PASS
- `tools/verify-app.ps1`（Release 构建，真实 UI Automation 驱动，含中英文两阶段）→ **66 通过 / 0 失败**，无 Render error

## 5. 已知未测 / 未修
- `TerminalSurface.CellFromPoint` 与页面滚动步长只能静态核对：`TerminalSurface` 需要 UI 线程与可视树，
  仓库没有 App 测试工程。建议在 Windows 10/11 真机上点选一列文本做一次人工确认（`docs/VERIFICATION.md` 已列出手工项）。
- `ScreenBuffer.Resize` 不做重排（缩窄再放宽会丢一行），这是 `ITerminalEmulator.Resize` 契约允许的既有行为，
  本次尝试实现重排时回归了 `ResizeTests.ResizeResetsScrollRegion`，已完整回退，未提交半成品。
- `FileEditorWindow` 的查找/替换把文本框（CRLF）偏移当作文档（LF）偏移，CRLF 文件上选择会逐行偏移；
  属 UI 交互缺陷，未修复。
- `docs/AGENT-CLI.md` 称"不带 --instance 时只允许一个桌面进程"，而 MCP Host 实际支持运行时重选/恢复；
  文档后续章节与测试都支持当前实现，未改文档。
- `RemoteMonitorSampler` 在一次慢采样跨过重新武装时可能对同一订阅重复通知一次（仅 UI 刷新层面）。
