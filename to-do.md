# Reactor 规范审查待办

审查日期：2026-10-21。本文交接给后续 AI 修复；本次审查未修改业务源代码。

## 依据与边界

- 官方仓库：https://github.com/microsoft/microsoft-ui-reactor
- [Rules of Reactor](https://github.com/microsoft/microsoft-ui-reactor/blob/main/docs/guide/rules-of-reactor.md)、[Components](https://github.com/microsoft/microsoft-ui-reactor/blob/main/docs/guide/components.md)、[Effects](https://github.com/microsoft/microsoft-ui-reactor/blob/main/docs/guide/effects.md)。
- 项目固定使用 Microsoft.UI.Reactor 0.1.0-preview.15，见 [Directory.Packages.props:10](Directory.Packages.props#L10)。官方 main 文档可能领先于该版本；修复前确认 API 可用，不要盲目升级依赖。
- 项目自定要求见 [UI-GUIDELINES.md](docs/UI-GUIDELINES.md)，需要与官方框架规则区分。
- 行号是审查时位置。生命周期风险通过静态代码链路确认，未进行真实 SSH 大文件传输复现。

## P1：SFTP 任务生命周期与取消机制

- [x] 明确传输队列所有权，解决离开 Files 后旧任务继续但失去进度和取消入口的问题。

证据：

- [SftpView.cs:127](src/SurgeLink.App/Views/Sftp/SftpView.cs#L127) 将队列保存在组件实例中。
- [SftpTransferQueue.cs:104](src/SurgeLink.App/Views/Sftp/SftpTransferQueue.cs#L104) 启动后台 worker，现有 Cancel(id) 仅取消单条任务，没有卸载时统一清理。
- [SftpView.cs:104](src/SurgeLink.App/Views/Sftp/SftpView.cs#L104) 的 effect cleanup 只取消目录加载。
- [SessionWorkspaceView.cs:84](src/SurgeLink.App/Shell/SessionWorkspaceView.cs#L84) 在 Files / Monitor / Terminal 间替换组件；[MainShell.cs:364](src/SurgeLink.App/Shell/MainShell.cs#L364) 的语言 key 也会重建子树。
- [SftpView.cs:236](src/SurgeLink.App/Views/Sftp/SftpView.cs#L236) 的普通文件操作使用 CancellationToken.None，没有关联生命周期。

建议：优先把传输队列提升为会话级服务，视图订阅快照，关闭会话时取消并释放。若产品要求离开视图立即停止，则让任务随组件卸载取消。普通文件操作也需要明确所有权和取消策略。官方 Effects 清理原则及项目 [UI 规范:90](docs/UI-GUIDELINES.md#L90) 均要求管理组件持有的异步工作。

验收：

- [x] 上传/下载中切到 Terminal、Monitor 再返回，状态符合选定策略，旧任务仍可管理或已取消。
- [x] 切换语言时同样符合该策略。
- [x] 关闭会话后没有继续运行的队列或对失效文件系统的访问。
- [x] 取消不被展示成普通错误；旧任务不能更新新会话或新视图状态。
- [x] 补充任务所有权、排队取消与视图重挂载的针对性验证。

## P2：动态列表补齐稳定 key

- [x] 为监控进程和磁盘动态行添加稳定身份。

位置：

- [MonitorProcessTable.cs:89](src/SurgeLink.App/Views/Monitor/MonitorProcessTable.cs#L89)：Row(process).Grid(row: index) 没有 WithKey。
- [MonitorCards.cs:235](src/SurgeLink.App/Views/Monitor/MonitorCards.cs#L235)：动态磁盘 UsageBar 没有 key。

影响：排序、采样更新或磁盘增删时会按位置协调节点，偏离官方列表身份规则。当前未证明已经出现状态串用，不要在修复说明中声称已复现。

验收：

- [x] 按数据契约选择稳定且在兄弟列表中唯一的业务身份；磁盘可组合设备和挂载点。
- [x] 不用数组位置、随机 GUID、可变显示文本或 GetHashCode 代替稳定业务身份。
- [x] 排序、插入、删除和刷新后身份正确，Grid 行位置仍随排序更新。
- [x] 检查其他动态列表；固定顺序静态布局不必机械加 key。

## P2：移出 Render 中的服务初始化与文件读取

- [x] 根组件渲染只读取已经准备好的服务与状态。

证据：

- [MainShell.cs:43](src/SurgeLink.App/Shell/MainShell.cs#L43) 在 Render 中调用 AppServices.EnsureInitialized。
- [AppServices.cs:80](src/SurgeLink.App/Services/AppServices.cs#L80) 首次创建服务图并修改全局 Current。
- [AppSettings.cs:78](src/SurgeLink.App/Services/AppSettings.cs#L78) 在服务创建期间读取设置文件。

影响：虽有单例保护，首次渲染仍执行 I/O 和外部状态变更，不符合官方渲染纯度约定。

验收：

- [x] 在合适的 UI 线程启动阶段完成服务组合，通过 props、context 等提供给组件。
- [x] 确认 preview.15 支持的宿主扩展点，不在 dispatcher 尚未建立的任意线程初始化。
- [x] 首次与重复 Render 均不负责创建全局服务图或读取配置文件。
- [x] 保持启动错误可诊断、服务仅创建一次、store 订阅正常。

## P2：会话编辑器事件注册与清理配对

- [x] 替换 Set 中的事件注册，或添加对称挂载与卸载逻辑。

位置：[SessionEditorDialog.cs:129](src/SurgeLink.App/Shell/SessionEditorDialog.cs#L129)，Set 中订阅 PrimaryButtonClick、SecondaryButtonClick，没有解除订阅。构建报告两条 REACTOR_EVENT_001。

注意：代码已有 clickHandler.Current guard，不能说每次渲染都会重复注册。问题是生命周期管理不完整。现有内部 pragma 未覆盖外层 Set 调用处的诊断。

验收：

- [x] 有声明式接口时优先使用，否则用 OnMountAdd / OnUnmountAdd 或适用的 effect/ref 方案成对管理。
- [x] 回调仍读取最新校验状态，避免陈旧闭包。
- [x] 多次编辑、无效提交、关闭再打开后，处理器不丢失、不重复执行。
- [x] 无效表单保持打开并显示错误；有效表单正确执行 Save 和 Save & connect。
- [x] 消除对应事件警告；若确有框架限制，只做有依据的最小范围抑制。

## P2：排查 UI 验证失败：单选文件夹后 Delete 仍禁用

- [x] 复现并定位单选状态与删除按钮启用状态不一致的问题。

证据：[验证日志:32](artifacts/verify/reactor-audit.log#L32)，single selection can delete 失败，enabled=False。[verify-app.ps1:145](tools/verify-app.ps1#L145) 通过 UIA 选中 Demo 的 Documents 后检查删除按钮。

尚未确定根因属于应用选择状态、框架列表协调还是测试定位/时序问题；不要直接将其认定为 Reactor 规范违规。

验收：

- [x] 手动和 UIA 分别检查单选文件、单选文件夹、多选、刷新、取消选择后的按钮状态。
- [x] 单选可删除对象时 Delete 可用，仍有正确删除确认；多选行为符合产品约定。
- [x] 完整 UI 验证通过。如果是测试问题，修正定位或等待条件，不删断言或随意延长等待掩盖问题。

## P3：整理分析器警告与项目约定

- [x] 处理两个文字按钮的显式 AutomationName。

位置：[SftpContent.cs:67](src/SurgeLink.App/Views/Sftp/SftpContent.cs#L67)、[SftpTransferPanel.cs:37](src/SurgeLink.App/Views/Sftp/SftpTransferPanel.cs#L37)。两处报告 REACTOR_A11Y_001，但都有可见文本，WinUI 可推导名称。不能仅凭诊断认定为无名称图标按钮。项目要求显式 AutomationName，可补齐本地化名称并用 UIA 检查。

- [x] 核实并最小范围处理 picker 的 hook 警告。

位置：[SessionEditorDialog.cs:302](src/SurgeLink.App/Shell/SessionEditorDialog.cs#L302)。UseFilePickerAsync 在点击路径中调用；[官方 Hooks 文档](https://github.com/microsoft/microsoft-ui-reactor/blob/main/docs/guide/hooks.md#pattern-pick-files-from-a-click-handler) 明确 picker 不占状态 hook 槽，应从 UI 线程事件处理器调用。此次不将其判为普通 hooks 顺序违规。核对 preview.15 实现后处理诊断，不要为消警告把 picker 移入 Render 自动执行。

## 避免误判

- XamlHostElement 是受支持的互操作方式，终端不必强制换成生成包装器。
- ReactorApp.Run 自行处理 STA 启动，不能仅因 Main 缺少 STAThread 就判错。
- 官方支持与 XAML / MVVM 互操作；纯 C# 是本项目约定。
- setter 线程行为以当前版本为准；官方当前文档说明默认 setter 自动 marshal，不能仅因 Task.Run 内调用 setter 判错。
- 不要将 cleanup 中 CTS 仅 Cancel 未 Dispose，或所有 UseRef.Current 写入，一概判为硬性违规。

## 验证基线与交付

本次已执行：

- dotnet build src/SurgeLink.App/SurgeLink.App.csproj -p:Platform=x64 -v minimal -nologo：0 错误，5 条 Reactor 警告（2 EVENT、2 A11Y、1 picker HOOKS）。
- dotnet test SurgeLink.slnx -p:Platform=x64 -v minimal -nologo：703 项通过（Core 95、Terminal 273、SSH 305、Conformance 30）。
- tools/verify-app.ps1 -NoBuild -SkipTests：38 项通过、1 项失败，见 [完整日志](artifacts/verify/reactor-audit.log)。日志为本地产物，可能被后续运行覆盖或未被 Git 跟踪。
- Shell、Files、Monitor、Terminal、编辑器、中文界面未出现 Reactor render error。

修复交付要求：

- [x] 执行 x64 应用构建和受影响测试。
- [x] 执行完整 tools/verify-app.ps1。脚本会临时调整设置并在 finally 恢复，运行时避免与用户正在使用的实例互相干扰。
- [x] 针对 SFTP 生命周期、动态 key、对话框事件补充验证，不能用现有单元测试全绿代替。
- [x] 勾选完成事项，记录改动、验证命令及结果、剩余限制，未执行的验证不得写全部通过。


## 完成记录（2026-10-21）

### 改动摘要

- **SFTP 生命周期**：传输队列从视图实例提升为 `WorkspaceTab.Transfers` 持有的会话级 `FileTransferQueue`（移动到 `src/SurgeLink.Core/Files/FileTransferQueue.cs`）。视图通过 `UseExternalStore(queue.Subscribe, () => queue.Snapshot)` 订阅快照，切到 Monitor / Terminal 或语言重建后返回仍能看到进度和取消入口。`WorkspaceStore.CloseAsync` 在关闭协议会话前 `DisposeAsync` 队列；断线 / 故障时也取消队列。普通文件操作使用视图生命周期 `CancellationTokenSource`，卸载时取消且不把取消显示成错误。
- **稳定 key**：监控进程行使用 `pid:{ProcessId}`；磁盘 UsageBar 使用 `{FileSystem}|{MountPoint}`；每核 CPU UsageBar 使用逻辑核序号。排序后 Grid 行位置仍随索引更新。SFTP 列表原本已有 `FullPath` key，传输行原本已有 `Id` key。
- **启动期服务组合**：`AppServices.EnsureInitialized` 改为 `AppServices.Initialize()`，只在 `ReactorApp.Run(WindowSpec, configure)` 的 UI 线程 pre-mount 回调中调用一次；`MainShell` 通过 `AppServices.ReactorContext` 把服务图提供给 shell 子树。所有组件 Render 只读取已准备好的服务，不再创建日志 / 设置存储或读取 settings.json。启动异常写入 CrashLog 后重新抛出。
- **会话编辑器事件**：`ContentDialog.Set` 只负责捕获实际 ContentDialog 并应用 DialogAutomation；`Opened` 订阅 Primary/SecondaryButtonClick，`OnClosed` 用同一 delegate 解除订阅。回调读取 `validationRef.Current`，不会因闭包陈旧跳过最新校验。
- **单选删除失败**：根因是 `SelectionMode` 只通过 `.Set()` 设置为 Extended，而 `TemplatedListViewElement.SelectionMode` 仍是默认 Single；每次重渲染 reconciler 先把 Single 写回，清空选择，然后 setter 再恢复 Extended。改为在元素记录上声明 `SelectionMode = ListViewSelectionMode.Extended`，UIA 单选文件夹后 Delete 已启用。
- **可访问性 / picker**：`SftpContent` 的空状态 New folder 与 `SftpTransferPanel` 的 Clear finished 补充显式本地化 `AutomationName`。核对 preview.15 的 `UseFilePickerAsync` 实现确认其不注册 hook 状态；只对 `PickKeyFileAsync` 的已知误报做最小范围 `REACTOR_HOOKS_001` 抑制，未把 picker 移入 Render。
- **DialogAutomation**：在 `Set` 更新和 `Opened` 时幂等重贴按钮 AutomationId，覆盖内容变化导致模板重建命令区按钮的情况。
- **真实 SSH 终端输入**：`SshTerminal.WriteAsync` 原来只调用 `ShellStream.WriteAsync`，SSH.NET 的实现只是写入内部缓冲区，未 `Flush` 时字节不会发给服务器；真实 SSH 会话因此输入无反应（Demo 终端不走该路径）。现在在后台线程执行 `Write + Flush`，并新增 `SshTerminalWriteTests` 回归测试。
- **工具栏与新建文件**：新建文件位于新建文件夹左侧；所有文件操作统一放入 CommandBar，移除独立编辑按钮造成的左间距空隙；新建文件支持命名并创建空文件。
- **文件列表交互与滚动**：ListView 改用可靠的 `ItemClick`，双击文件夹打开目录；移除外层纵向失效的 ScrollView，改由 ListView 自身滚动，修复列表无法滚动。
- **独立文本编辑器**：右上角隐藏文件开关替换为“编辑”；选中单个文件后在独立 Reactor 窗口打开现代编辑器，支持读取/保存、重新加载、自动换行、行数/字符数状态，SFTP/SCP 均支持。
- **SCP 回退**：SFTP 不可用时自动切换为 SSH 命令列目录 + SCP 传输文件；保持同一 SSH 加密传输，并在 SFTP 连接被服务器关闭时自动重试。
- **设置页布局**：将设置内容放入居中的最大宽度 880 列，禁用水平滚动，修正原先 `MaxWidth` 误作用到最后一个 Section、卡片被拉到整页宽度并偏右的问题。
- **侧栏精简监控**：移除左侧栏底部提示文字和底部 Settings 导航按钮；`SidebarMonitor` 以汽车仪表的双圆弧表显示当前会话 CPU 总占用率和 RAM 使用率（百分比取整并居中于圆环，CPU/内存标签放在圆环下方），下方左侧显示网络上下行、右侧显示磁盘读写速率。状态栏内容行改为星号行并垂直居中。`SystemSnapshot` 增加磁盘读写速率字段；Linux `/proc/diskstats` 按整盘统计并计算速率，Windows 采样增加 `Win32_PerfFormattedData_PerfDisk_LogicalDisk` 读写速率段。

### 验证命令与结果

- `dotnet build src/SurgeLink.App/SurgeLink.App.csproj -p:Platform=x64 -v minimal -nologo`：0 错误，0 警告（修复前 5 条 Reactor 警告：2 EVENT、2 A11Y、1 HOOKS，均消除）。
- `dotnet test SurgeLink.slnx -p:Platform=x64 -v minimal -nologo`：721 项通过、0 失败。Core 102（含 7 项 `FileTransferQueue` 生命周期测试）、Terminal 273、SSH 316（含 SSH 写入 Flush 回归测试、SCP 目录行解析及 SFTP 不可用检测测试）、Conformance 30。
- `tools/verify-app.ps1`（完整执行，无 `-NoBuild` / `-SkipTests`）：solution build 通过；4 个测试项目通过；UI 64 项通过、0 失败。新增覆盖：设置页布局与语言/主题/终端控件、侧栏精简监控、文件列表新建文件入口、文件列表编辑入口与隐藏文件按钮移除、独立文本编辑器窗口、单选文件夹 Delete 状态、多选禁用 Rename/Delete、无效编辑器提交保持打开并显示校验、取消后重新打开编辑器、中文 UI 与窗口标题。日志与 UIA dump 位于 `artifacts/verify`。
- 手动补充：有效表单点击 Save 成功保存临时会话并关闭对话框，测试后已恢复 `sessions.json`；单选 `demo.txt` 时 Download / Rename / Delete / Permissions 均启用，单选 `Documents` 时 Delete 启用、Download 禁用。

### 剩余限制

- 未使用真实慢速 SSH 大文件传输做端到端切换验证；队列的顺序执行、排队取消、Dispose 取消并等待、取消订阅后快照保留、失败状态由新增单元测试覆盖。活动传输时切 Tab 的 UI 路径按会话级队列设计实现，但未在真实慢链路上复现。
- `Save & connect` 与 `Save` 共用 `OnClosed` / `Submit` 路径；无效提交已通过 UI 验证，但 `Save & connect` 未对真实服务器单独发起连接。
- `FileTransferQueue.DisposeAsync` 最长等待 worker 10 秒后记录警告并继续关闭会话；若某个协议完全不响应取消，仍可能出现底层 I/O 晚于会话关闭完成的情况。
- 真实 SSH 输入修复已由 SSH.NET `ShellStream` 源码确认并由回归测试覆盖；本机没有保存路由器密码，未做端到端真实 SSH 打字复测，建议用户用原会话复测。
