# 代码逻辑审查

日期：2026-10-23。下方发现保留为修复前的审查证据，行号对应修复前源码。

## 修复结果

- 连接完成仅向仍存在且连接参数匹配的记录合并最近使用时间和系统信息；与保存、删除操作串行，保留新的名称等字段，不复活删除记录。重连解析最新保存配置。
- 界面重连复用普通连接的凭据询问、并发保护与取消流程，在取得凭据前保留原标签。未保存的私钥口令可以重新输入；未加密密钥允许留空。
- 编辑器保留 UTF-8、UTF-16 LE/BE、UTF-32 LE/BE 编码及 BOM 策略，保存沿用原格式；解码严格拒绝无效字节。
- 终端支持 DSR 状态、普通/私有光标位置报告、主/次设备属性及 ESC Z 查询，通过 TerminalBridge 回写远程通道，释放时取消订阅。尚未实现所有 xterm 扩展查询。
- 修复后全套离线测试 844 项通过、0 失败、0 跳过（Terminal 285、Core 198、SSH 321、Conformance 40）。新增 23 个回归用例覆盖配置迟到写回、重连配置与临时凭据、取消保护、编码往返、查询字节和回写生命周期。
- x64 Release 应用构建成功，0 警告、0 错误。真实服务器、桌面 UI 和跨平台实机验证边界仍如下所述。

## 验证结果与范围

- `dotnet test SurgeLink.slnx -c Release -p:Platform=x64 --nologo`：821 项通过，0 失败、0 跳过（Terminal 276、Core 184、SSH 321、Conformance 40）。
- `dotnet build src/SurgeLink.App/SurgeLink.App.csproj -c Release -p:Platform=x64 --no-restore --nologo`：成功，0 警告、0 错误。
- 静态核查重点：会话创建和释放、凭据解析、重连、SFTP 操作、传输队列、安全提交、文本编辑、终端输入输出和监控采样。
- 没有执行真实服务器 SSH/SFTP/SCP、桌面 UI 端到端、安装包安装、Windows 10 或 ARM64 实机验证。离线契约测试使用 Demo，不能证明真实 SSH 功能均正常。本次不宣称逐行穷尽所有代码或所有功能已通过验收。

## 发现

### P1：重连使用旧配置并可能覆盖用户的新配置

位置：`src/SurgeLink.App/Sessions/WorkspaceStore.cs:288`、`:198`。

标签保留打开时的 `Profile`。用户在连接期间编辑同一保存会话的主机、端口、用户名或名称后，重连仍读取 `tab.Profile`，连接旧目标，并在成功时将旧对象 `MarkUsed` 后完整 Upsert，覆盖刚保存的配置。删除保存会话但保留标签后重连，也会重新创建已删除的条目。正常连接的异步探测期间发生编辑或删除，同样存在迟到写回。

建议：重连明确解析当前保存配置；更新最近使用时间及系统信息时，仅更新仍存在且连接目标匹配的记录，避免整条旧快照 Upsert。补充编辑/删除与连接完成交错的回归测试。

### P2：未保存密码的连接无法通过重连入口再次认证

位置：`src/SurgeLink.App/Sessions/WorkspaceStore.cs:290`、`src/SurgeLink.App/Shell/MainShell.cs:268`。

普通连接经过 `BuildConnectOptionsAsync`，没有保存密码时会请求输入；重连先关闭原标签，然后直接 `OpenAsync(profile, null, ...)`，绕过该流程。SessionManager 只能从持久化凭据取值，而 SSH 工厂拒绝空密码，因此一次性密码登录后点击重连必然失败，且旧标签已被移除。仅本次输入的加密私钥口令也会丢失。

建议：重连复用正常连接的凭据询问、取消及连接调度逻辑，在关闭旧标签前获得所需输入。

### P2：文本保存隐式改变原文件编码

位置：`src/SurgeLink.Core/Files/BoundedTextFile.cs:23`、`src/SurgeLink.App/Views/Sftp/FileEditorWindow.cs:219`。

读取启用 BOM 编码检测，接受 UTF-16/UTF-32 和带 BOM 的 UTF-8；返回结果只有字符串。保存固定写为无 BOM UTF-8，未保留原编码，也没有转换提示。因此即使未修改文本点击保存，也可能改变文件字节和编码，使依赖 UTF-16 等格式的远程程序无法正常读取。

建议：读取结果携带编码及 BOM 信息并原样保存，或明确限制支持的输入编码，并在转换前给出说明。增加不同 BOM 文件的读写回归。

### P2：终端查询没有响应通道

位置：`src/SurgeLink.Terminal/TerminalEmulator.cs:545`。

CSI 默认分支明确忽略 DSR/DA；TerminalBridge 仅解析输出和发送用户输入，没有模拟器查询响应回写。远程程序发送 `ESC[6n` 查询光标位置时收不到 CPR 响应，依赖该响应的程序可能超时或持续等待，构成当前 xterm 兼容性缺口。具体程序影响尚未通过真实服务器验证。

建议：增加模拟器响应事件或响应队列，接入终端写入通道，优先支持状态查询、光标位置报告和设备属性查询，并测试返回字节及当前坐标。
