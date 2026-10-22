# 源码复审与待补充事项

日期：2026-10-22。重点覆盖文件读写、会话生命周期、UI 选择适配及发布边界。初始审查记录已随 `e3a6c28` 提交；下方问题描述保留为修复前证据，行号对应当时源码。

## 修复结果

| 问题 | 已实现的保护 |
| --- | --- |
| 下载目录逃逸 | UI 和队列写入边界均校验 Windows 文件名、保留名称与规范化路径 |
| 原生列表项类型不匹配 | 通过原生容器/选择索引映射当前排序后的业务条目 |
| SCP 取消仍上传 | 写入失败或取消后不提交缓冲；上传接受取消信号并断开传输连接 |
| 覆盖失败损坏原文件 | 上传/编辑/复制先写临时文件再安全提交；下载临时写入且拒绝覆盖 |
| 编辑器修改丢失 | 关闭/重新加载确认、保存快照比较、会话关闭取消、保存失败保留文本 |
| 会话生命周期泄漏 | 释放时取消在途连接并拒绝迟到注册；工作区初始化失败回滚资源 |
| 复制目标竞态 | 提交操作本身拒绝覆盖并发创建的目标 |
| 编辑器无界读取 | 实际字节读取限制 4 MiB，拒绝无效 UTF-8 和含 NUL 的内容 |

修复回归：Terminal 273、Core 149、SSH 319、Conformance 40，共 781 项通过。Release x64 与 ARM64 编译均为 0 警告、0 错误。离线 Demo 界面确认右键条目启用、Ctrl+C、跨目录粘贴、未保存关闭确认与编辑保存。规则扫描 322 个文件、0 命中。文件列表外的空白区域右键仍未弹出菜单，已撤回未通过验证的额外尝试；可通过文件条目菜单或聚焦列表后 Ctrl+V 粘贴。

协议安全提交限制见 [新增协议指南](ADDING-A-PROTOCOL.md)。尚未进行真实 SSH/SFTP/SCP 故障注入、ARM64 实机和 Store/WACK 认证；下方发布能力建议仍是后续工作。

## 需要优先修复

### P1：下载文件名可以逃逸用户选定目录

位置：`src/SurgeLink.App/Views/Sftp/SftpOperations.cs:89–96`；`src/SurgeLink.Core/Files/FileTransferQueue.cs:275–301`。

CreateDownload 直接执行 `Path.Combine(localFolder, entry.Name)`。远程 Linux 文件名允许反斜杠和冒号，而 Windows 会将它们解释为目录分隔符、盘符或数据流。服务器返回 `..\outside.txt` 时，目标会解析到下载目录的上一级；返回 `C:\AuditSandbox\outside.txt` 则直接改变盘符和目标目录。队列随后用 FileMode.Create 打开该路径，可覆盖当前用户有权限写入的文件。

验证：使用 Windows 的实际 Path.Combine/GetFullPath 规则复现路径解析，未写入任何目录外文件，也未连接真实服务器。

建议：下载前拒绝包含 Windows 非法字符、分隔符、绝对路径、保留名称及数据流语法的远程名称，并验证最终规范化路径位于选择目录内；同名文件显式决定覆盖策略。补充普通单元测试。

### P1：文件右键菜单和快捷键读取了错误的列表项类型

位置：`src/SurgeLink.App/Views/Sftp/SftpView.cs:370–378`、`:482–493`。

`ListView<RemoteFileEntry>` 的 Reactor 原生 ItemsSource 不是 RemoteFileEntry 集合。当前版本使用 ReactorRow，兼容路径使用 int。代码却对 SelectedItem、SelectedItems 和 ItemFromContainer 直接做 RemoteFileEntry 转换，得到 null 或空集合。结果包括右键剪切/复制/属性不可用，Ctrl+C/X 不捕获项目，以及 Enter/F2/Delete 无法获得当前文件。

依据：核对当前 NuGet 对应提交 `f17ce2dd12083e7e8709883c7c4ccd04c556810d` 的 `src/Reactor/Core/Reconciler.KeyedItemsBinding.cs`。其 SelectionChanged trampoline 明确将 ReactorRow.Index/int 转换为业务项目索引。上次截图时观察到的菜单禁用状态也与此一致；本轮没有重新进行 UI 交互复现。

建议：通过 Reactor 的类型化选择回调保存选择；右键定位用容器索引映射到当前排序/过滤后的 rows。不要访问框架内部 ReactorRow 类型。补充菜单选中未选中项、多选、排序后操作与键盘路径验证。

### P1：SCP 上传取消后仍会提交缓冲内容

位置：`src/SurgeLink.Protocols.Ssh/ScpFileSystem.cs:797–815`；调用方 `src/SurgeLink.Core/Files/FileTransferQueue.cs:245–272`。

SCP 写入流先写本地临时文件，DisposeAsync 无条件调用 UploadFileAsync，并传入 CancellationToken.None。上层取消/写入异常触发 await using 的清理时，仍会上传已经写入的部分文件，并可能覆盖远程同名文件；取消状态与远程实际副作用不一致。

建议：区分完成提交和异常/取消释放，只有显式完成才上传；将取消信号传入真正的上传过程，失败时保留原目标。补充“写入中取消后不得上传”的协议实现测试。

### P1：覆盖写入缺少临时文件提交，失败可能破坏原文件

位置：`src/SurgeLink.Core/Files/FileTransferQueue.cs:289`；`src/SurgeLink.Protocols.Ssh/SftpFileSystem.cs:159`；`src/SurgeLink.App/Views/Sftp/FileEditorWindow.cs:99–110`。

下载直接以 FileMode.Create 截断本地同名目标；SFTP 上传及编辑保存也以 Create 打开最终远程路径。断线、取消或写入失败后，原文件可能已经变成空文件或残缺内容。

建议：先写同目录的唯一临时文件，成功刷新并关闭后再提交替换；协议接口需明确原子替换或相应降级策略。上传和下载均补充中途失败保持原文件的测试。

### P1：编辑器缺少未保存内容保护

位置：`src/SurgeLink.App/Views/Sftp/FileEditorWindow.cs:113`、`:124–128`、`:218–225`。

Reload 不检查 dirty，关闭窗口也没有未保存提示，会直接丢弃编辑内容。保存期间文本框仍能继续编辑，而保存完成后无条件 setDirty(false)，会把保存开始后新增的内容标记为已保存。此外，保存使用 CancellationToken.None，关闭窗口或会话后没有统一的取消/等待策略。

建议：关闭/重新加载前确认；保存时记录文本版本，仅在版本仍匹配时清除 dirty；将编辑器窗口生命周期与会话生命周期关联，并明确保存中关闭的行为。

## 其他可靠性问题

### P2：连接与释放之间存在竞态，异常路径缺少回收

位置：`src/SurgeLink.Core/Sessions/SessionManager.cs:56–73`；`src/SurgeLink.App/Sessions/WorkspaceStore.cs:151–194`。

SessionManager 只在等待连接前检查 _disposed。若 DisposeAsync 在 ConnectAsync 等待期间完成，后续连接成功仍会加入 _sessions。独立离线复现得到：已完成 DisposeAsync 的管理器内仍有 1 个会话。

WorkspaceStore 在终端启动、资料写盘或后续工作失败时，仅在 finally 清除 IsConnecting，没有回滚已创建会话、桥接器及事件订阅，可能留下界面不可见的连接。

建议：在同一同步边界内完成连接注册与已释放检查；释放在途连接；工作区初始化使用所有权转移和失败回滚。临时复现代码放在被忽略的 artifacts/review-repro 中，未加入正式测试项目。

### P2：远程复制的“目标不存在”检查存在竞争窗口

位置：`src/SurgeLink.Core/Files/RemoteFileCopy.cs:35–56`。

复制先 ListAsync 检查名称，之后 OpenWriteAsync 使用的是会覆盖的写入接口。其他客户端在两次操作间创建目标时仍会被覆盖，不能保证界面声明的“同名目标不覆盖”。

建议：协议能力提供 CreateNew/独占创建语义；不要把事前目录检查当成写入时的保证。

### P2：编辑器大小限制只检查旧的元数据

位置：`src/SurgeLink.App/Views/Sftp/FileEditorWindow.cs:61–72`。

4 MiB 限制依据打开时的 target.Size，实际读取是无界 ReadToEndAsync。文件在列表刷新后增长，或服务器返回不准确的长度时，仍可能读取大量数据并使编辑窗口失去响应。

建议：对实际读取字节数设置上限，再解码文本；同时处理无效编码和二进制文件。

## 建议补充的发布能力

- 在关于/设置界面提供隐私政策和开源许可的直接入口；当前政策文件已存在，应用内没有对应入口。
- 增加日志保留天数、总大小上限和导出前脱敏。当前日志按天命名但没有清理规则。
- 完成干净环境的安装、启动、升级、卸载及 ARM64 实机验证，并保留 Store/WACK 结果。
- 配置仓库依赖更新与工作流静态检查；发布前保留与二进制对应的源码版本记录。
- 将上述边界补充到现有单元测试与协议契约测试，不需要恢复已删除的黑盒测试模块。

## 本轮验证

- .NET 测试：Terminal 273、Core 125、SSH 316、Conformance 36，共 750 项通过。
- 当前工作树规则扫描：313 个文件，常见秘密规则 0 命中；这不等于排除所有秘密。
- NuGet 已知漏洞查询：9 个项目均未报告已知易受攻击包。
- Windows 下载路径解析：复现目录逃逸，不执行实际文件写入。
- SessionManager 在途连接释放竞态：离线复现，释放后仍注册 1 个会话。
- 没有执行真实 SSH/SFTP/SCP 故障注入、渗透测试或商店认证。本轮未重新扫描历史提交。

框架依据：[当前依赖对应的列表适配源码](https://github.com/microsoft/microsoft-ui-reactor/blob/f17ce2dd12083e7e8709883c7c4ccd04c556810d/src/Reactor/Core/Reconciler.KeyedItemsBinding.cs)。
