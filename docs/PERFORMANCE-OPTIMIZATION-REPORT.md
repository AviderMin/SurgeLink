# SurgeLink 性能优化报告

- **报告日期**：2026-11-01
- **审查范围**：`src` 下全部生产代码（约 26,500 行 C#），重点为 SurgeLink.Terminal、SurgeLink.Protocols.Ssh（含 Serial/Telnet/Demo）、SurgeLink.App、SurgeLink.Core 与 Agent/AgentHost/Cli 层
- **审查方式**：四路并行静态代码审查（终端模拟器 / 协议层 / UI 层 / 核心与 Agent 层），逐文件阅读热路径并交叉验证；共记录 **54 条**性能发现（高 10、中 22、低 22）。第 2 章按主题将 10 条高严重度发现归并为 9 组，个别中/低条目在第 3、4 章中与对应高严重度问题合并列出
- **详细依据**：各模块完整发现清单（含代码片段与改造伪代码）见 [artifacts/perf-review/](../artifacts/perf-review/) 下的 [terminal.md](../artifacts/perf-review/terminal.md)、[protocols.md](../artifacts/perf-review/protocols.md)、[app.md](../artifacts/perf-review/app.md)、[core.md](../artifacts/perf-review/core.md)

---

## 1. 总体评估

SurgeLink 的整体架构质量良好：终端模拟器采用行引用移动 + 环形历史 + 行池、AnsiParser 固定缓冲零分配、ASCII 单字符缓存、渲染层行级 diff、监控采样器锁外通知与快照节流、存储层原子写 + 跨进程锁文件——这些都是经过深思熟虑的设计，应予保持。

问题集中在四条主线上，均属于"单点局部合理、规模放大后失效"的类型：

| 主线 | 表现 | 涉及模块 |
|---|---|---|
| **A. 远端元数据往返放大** | 每文件全量列举父目录 / 单条目 stat 拉全目录、每文件 6~9 个固定元数据往返 | Core、Protocols.Ssh |
| **B. UI 线程与锁内的同步 IO** | 设置/凭据/DPAPI 同步读写、输出缓冲锁内搬移、锁内派发事件与依赖属性 | App、Core、Terminal |
| **C. 热路径分配与拷贝** | 读循环逐块 new byte[]、非 ASCII 每字形字符串驻留、Resize 全历史重分配 | Protocols.Ssh、Terminal |
| **D. 渲染放大** | 整壳重渲染、终端强制全量重绘、编辑器每键 O(文档)、每渲染 O(n·m) 恢复选区 | App |

---

## 2. 高严重度问题（P0，建议优先处理）

### 2.1 【A/B】远端逐项全量列举父目录，形成 O(N²) 流量与往返放大
- **位置**：[RemoteFileCopy.cs:35-38](../src/SurgeLink.Core/Files/RemoteFileCopy.cs:35)、[RemoteFileWrite.cs:41-46](../src/SurgeLink.Core/Files/RemoteFileWrite.cs:41)、[FileTransferQueue.cs:346-356](../src/SurgeLink.Core/Files/FileTransferQueue.cs:346)、[SftpFileSystem.cs:495](../src/SurgeLink.Protocols.Ssh/SftpFileSystem.cs:495)
- **现状**：复制/上传/删除/提交每一步都 `ListAsync` 全父目录后线性查找目标名；SFTP 的 `GetEntryNoFollowAsync` 用全目录 READDIR 代替单条目 stat。N 个文件 = O(N²) 条目流量；叠加 staging 目录创建、2×chmod、提交、2×删除，每文件固定 6~9 个元数据往返。
- **影响**：大目录（千条目级）批量操作网络量放大 1~2 个数量级；高延迟链路吞吐被 RTT 完全支配。
- **建议**：目录级列举结果缓存（带失效）+ 单条目 stat 语义（保真 lstat/符号链接判定）；staging 操作批量化，合并往返。改动风险低~中。

### 2.2 【B】TerminalOutputBuffer 满容量后每追加 4KB 锁内整体搬移约 256KB
- **位置**：[TerminalOutputBuffer.cs:28-32](../src/SurgeLink.Core/Terminal/TerminalOutputBuffer.cs:28)，调用点 [TerminalBridge.cs:199-208](../src/SurgeLink.App/Sessions/TerminalBridge.cs:199)
- **现状**：StringBuilder 满 128K 字符后每追加 4KB 就 `Remove(0,k)` 整体前移；解码/裁剪/读取共用一把锁，且 `_output.Append` 在 `Emulator.SyncRoot` 下执行，与 UI Paint 的全网格 Capture 竞争同一把锁。
- **影响**：输出风暴（`cat` 大文件、编译日志）下约 600MB/s 无谓 memmove；terminal.output 读端尾延迟被拖垮，输出高峰期 UI 掉帧。
- **建议**：改分段环形缓冲（保持游标/代理项语义）；App 侧将 Append 移出 SyncRoot。中等风险，接口可对照现有测试。

### 2.3 【C】SCP fallback 用单一 _transferLock 串行化所有传输
- **位置**：[ScpFileSystem.cs:24](../src/SurgeLink.Protocols.Ssh/ScpFileSystem.cs:24)、[:110](../src/SurgeLink.Protocols.Ssh/ScpFileSystem.cs:110)、[:292](../src/SurgeLink.Protocols.Ssh/ScpFileSystem.cs:292)
- **现状**：所有下载/上传全程持同一把传输锁，下载与上传互相阻塞；而下载走独立 exec 通道本不需要该锁。
- **影响**：SCP 回退场景多文件队列、双向传输吞吐被限为单流。
- **建议**：按方向/通道拆分锁粒度，仅串行化真正共享的资源；需补并发传输测试并重审 ScpClient 断开竞态。

### 2.4 【C】SshTerminal 读循环每 4KB 一次 new byte[] + BlockCopy
- **位置**：[SshTerminal.cs:327-329](../src/SurgeLink.Protocols.Ssh/SshTerminal.cs:327)，缓冲默认值 [SshProtocolOptions.cs:43](../src/SurgeLink.Protocols.Ssh/SshProtocolOptions.cs:43)
- **现状**：租用池缓冲后仍为每个 read 结果新建数组拷贝，且 `ShellBufferSize` 默认仅 4KB。
- **影响**：高吞吐输出 GC 压力大、读取次数多。
- **建议**：默认缓冲升至 32~64KB；chunk 改池化或调整事件消费契约延迟归还。风险低-中，收益直接。

### 2.5 【C】Resize 逐行重分配全部 scrollback 行
- **位置**：[TerminalEmulator.cs:173-178](../src/SurgeLink.Terminal/TerminalEmulator.cs:173)、[CellLine.cs:28-45](../src/SurgeLink.Terminal/CellLine.cs:28)
- **现状**：每次 Resize 对最多 10,000 行历史各分配一个新 `TerminalCell[columns]` 并拷贝；读路径 `GetBufferCell` 已做列钳制，该循环在正确性上并不必要。
- **影响**：拖拽窗口每帧触发，200 列时约 60+MB 分配/拷贝，卡顿 + GC 抖动。
- **建议**：直接移除或懒惰化（读时按需扩列）；补 resize 回归测试。风险低、收益高。

### 2.6 【C】非 ASCII 每码点分配新字符串并永久驻留于单元格与滚动历史
- **位置**：[TerminalEmulator.cs:357-381](../src/SurgeLink.Terminal/TerminalEmulator.cs:357)
- **现状**：CJK/emoji 输出每字形一次 `rune.ToString()` 堆分配，随单元格进入历史。
- **影响**：全 CJK 200×10,000 历史仅字符串就约 100MB；中文用户是主力场景。
- **建议**：单码点字符串缓存（扩展 AsciiText 为按需 ConcurrentDictionary），重复字形复用实例，分配可降 90%+、历史内存降数倍。中等风险（需保 Text 语义与组合标记行为）。

### 2.7 【B】设置更新在 UI 线程同步读写 JSON 文件
- **位置**：[AppSettings.cs:109-130](../src/SurgeLink.App/Services/AppSettings.cs:109)
- **现状**：`Update` 走 `AtomicJsonFile.WithExclusiveAccess` 同步读 + 全量写 + 落盘；Ctrl+滚轮字体缩放每档一次磁盘往返并触发全订阅者重渲染，设置保存、监控间隔切换同理。
- **影响**：UI 线程磁盘 IO，交互毛刺。
- **建议**：异步写队列 + 关闭时冲刷 + 写序/崩溃一致性保证。中等风险。

### 2.8 【B】会话编辑对话框每个按键同步读凭据文件并做 DPAPI 解密
- **位置**：[SessionEditorDialog.cs:342](../src/SurgeLink.App/Shell/SessionEditorDialog.cs:342) → [EncryptedCredentialStore.cs:44-63](../src/SurgeLink.Core/Security/EncryptedCredentialStore.cs:44)
- **现状**：每次渲染（含每键输入）调 `Credentials.Get`，后者全量重读文件 + JSON 解析 + 最多 2 次 DPAPI 解密，同步阻塞 UI。
- **影响**：编辑对话框打字延迟每键数 ms~数十 ms。
- **建议**：按对话框生命周期缓存凭据快照；与问题 3.3 的 JSON 存储缓存一并解决。风险低。

### 2.9 【D】远程编辑器每键 O(文档)：全文拷贝 + 行索引重建 + 整窗重渲染
- **位置**：[FileEditorSurface.cs:56-69](../src/SurgeLink.App/Views/Sftp/FileEditorSurface.cs:56)、[FileEditorWindow.cs:44](../src/SurgeLink.App/Views/Sftp/FileEditorWindow.cs:44)、[:331](../src/SurgeLink.App/Views/Sftp/FileEditorWindow.cs:331)，叠加 [LineLayout.cs:31-59](../src/SurgeLink.Core/Files/LineLayout.cs:31)、[EditorTextBuffer.cs:15-72](../src/SurgeLink.Core/Files/EditorTextBuffer.cs:15)
- **现状**：`TextChanged` 全文 `ToDocumentText` 拷贝 + `new EditorLineIndex` 全量重建；再叠加每渲染 dirty 全串比较 + `CountLines` 全扫；`LineAt`/偏移换算每次从头逐字符扫描，行号栏每变一次整串重建。已有的 `EditorLineIndex` int[] 二分没有被复用。
- **影响**：4MB 文件连续打字 CPU/GC 压力线性增长，光标移动也是全量扫描。
- **建议**：行索引增量更新（处理换行边界）、调用方统一走二分行索引、行号栏增量维护。中等风险，收益大。

---

## 3. 中严重度问题（P1）

| # | 问题 | 位置 | 要点 |
|---|---|---|---|
| 1 | 编辑器保存双读全文件 + 双哈希 | [RemoteTextEdit.cs:16-24](../src/SurgeLink.Core/Files/RemoteTextEdit.cs:16)、[RemoteFileWrite.cs:40](../src/SurgeLink.Core/Files/RemoteFileWrite.cs:40) | 每次保存 = 下载两次（解码 + SHA256）+ 上传一次，大文件延迟 ×3；写后校验可降级为 Stat 比对 |
| 2 | LiteralTextSearch 反向查找最坏 O(n×m) | [LiteralTextSearch.cs:16-21](../src/SurgeLink.Core/Files/LiteralTextSearch.cs:16) | 逐位置回退比较；改切片 LastIndexOf 向量化，单函数替换 |
| 3 | JSON 存储每次查找全量重读 + 锁内 DPAPI/锁文件自旋 | [EncryptedCredentialStore.cs:44-63](../src/SurgeLink.Core/Security/EncryptedCredentialStore.cs:44)、[JsonHostKeyStore.cs:21-77](../src/SurgeLink.Core/Security/JsonHostKeyStore.cs:21) | 每次连接/主机密钥验证重读全文件 + 反序列化；锁文件 20ms 自旋最长 5s 且 DPAPI 在锁内，并发连接互相阻塞；加内存缓存 + 时间戳失效、DPAPI 移出锁 |
| 4 | AgentGateway 全局锁内调 UI 回调 + 每会话读写全串行 | [AgentGateway.cs:128-139](../src/SurgeLink.Agent/AgentGateway.cs:128) | 慢回调卡住整个网关；只读（screen/output/files.read）与输入写全串行，MCP 并发读被慢 files.read 阻塞 |
| 5 | DesktopConnection 每请求进程枚举 + 250ms 轮询 | [DesktopConnection.cs:27-40](../src/SurgeLink.AgentHost/DesktopConnection.cs:27)、[:311-325](../src/SurgeLink.AgentHost/DesktopConnection.cs:311) | 自动路由每 MCP 请求做进程枚举；退出监视每 250ms 全量枚举；加 TTL 缓存 + 事件驱动 |
| 6 | RemoteReadStream 拷贝链 + 同步阻塞 channel 写 | [RemoteReadStream.cs:118](../src/SurgeLink.Protocols.Ssh/RemoteReadStream.cs:118) | 每字节 2-3 次拷贝、每 32KB ToArray；GetAwaiter().GetResult() 阻塞写 channel，GB 级下载受益明显 |
| 7 | SCP 上传先完整落本地临时文件 | [ScpFileSystem.cs:144](../src/SurgeLink.Protocols.Ssh/ScpFileSystem.cs:144) | 双倍磁盘 IO、大文件无传输中进度；需保留原子提交语义 |
| 8 | 主机信息探测串行 5-7 条远程命令 | [SshSession.cs:330-412](../src/SurgeLink.Protocols.Ssh/SshSession.cs:330) | 高延迟链路会话就绪慢 1 秒+；合并为单脚本一次往返 + 分段解析 |
| 9 | Windows 监控每 2 秒拉起 powershell + 8 组 CIM 查询 | [WindowsMonitorScript.cs:85](../src/SurgeLink.Protocols.Ssh/Monitoring/WindowsMonitorScript.cs:85) | network fallback 段无条件执行，监控本身成远端负载源；拆命令 + 调间隔 |
| 10 | TelnetCodec 逐字节 List + ToArray | [TelnetCodec.cs:53-138](../src/SurgeLink.Protocols.Telnet/TelnetCodec.cs:53) | 每次收发都重新分配；改 span/池化缓冲 |
| 11 | TelnetTerminal 读解析与写共用 _ioGate | [TelnetTerminal.cs:59](../src/SurgeLink.Protocols.Telnet/TelnetTerminal.cs:59)、[:142](../src/SurgeLink.Protocols.Telnet/TelnetTerminal.cs:142) | 慢写拖停读；锁外写需保协商应答顺序 |
| 12 | SshCommandRunner 输出捕获双倍拷贝 | [SshCommandRunner.cs:105](../src/SurgeLink.Protocols.Ssh/SshCommandRunner.cs:105) | MemoryStream ToArray 整份拷贝；GetBuffer 等价替换 |
| 13 | Feed 持锁解析整块数据、锁内同步派发事件 | [TerminalEmulator.cs:133-155](../src/SurgeLink.Terminal/TerminalEmulator.cs:133)、[:763-777](../src/SurgeLink.Terminal/TerminalEmulator.cs:763) | DSR/CPR 应答若同步写 SSH 通道即锁跨 IO；应答排队锁外派发 + 16~32KB 分片解析 |
| 14 | AnsiParser 无 ASCII 批量快路径 | [AnsiParser.cs:60-114](../src/SurgeLink.Terminal/AnsiParser.cs:60) | MB 级纯 ASCII 流量每字节付 switch 分发；Ground 态批量处理可提 2~5× |
| 15 | 任一 workspace 变更触发整壳重渲染 + 终端强制全量重绘 | [TerminalView.cs:46-67](../src/SurgeLink.App/Views/Terminal/TerminalView.cs:46)、[TerminalSurface.cs:176-181](../src/SurgeLink.App/Views/Terminal/TerminalSurface.cs:176) | props record 含新鲜 lambda 恒不相等；SFTP 导航/切 tab 时多终端场景每次事件数万格快照 |
| 16 | AgentPanel 常驻 5s DispatcherTimer 仅触发重渲染 | [AgentPanel.cs:33-41](../src/SurgeLink.App/Shell/AgentPanel.cs:33) | 面板隐藏时仍每 5s 全量重建，周期性 CPU/分配尖峰 |
| 17 | Paint() 在 SyncRoot 锁内设置 ScrollBar 依赖属性 | [TerminalSurface.Paint.cs:40-49](../src/SurgeLink.App/Views/Terminal/TerminalSurface.Paint.cs:40) | DP 回调耗时不可控，放大 feed 线程阻塞窗口 |
| 18 | SFTP 选区恢复 O(rows×selected) WinRT 跨界访问 | [SftpView.cs:701-717](../src/SurgeLink.App/Views/Sftp/SftpView.cs:701)、[:468-470](../src/SurgeLink.App/Views/Sftp/SftpView.cs:468) | 大目录右键/删除/刷新迟钝；Contains/IndexOf 改集合化 |
| 19 | WorkspaceSnapshot 派生数据每渲染全量重算 | [WorkspaceStore.cs:88-124](../src/SurgeLink.App/Sessions/WorkspaceStore.cs:88) | FilteredProfiles 全排序、ConnectedServerCount 逐 tab 解析 IP；缓存派生值 |
| 20 | PrivacyDisplay.Mask 每调用重建主机列表 + 正则 | [PrivacyDisplay.cs:13-25](../src/SurgeLink.App/Components/PrivacyDisplay.cs:13) | 隐私模式下每渲染每标签 2-4 次；缓存主机数组/编译正则 |
| 21 | 输出缓冲 Append 在 SyncRoot 内（见 2.2） | — | 与 2.2 合并处理 |

---

## 4. 低严重度问题（P2，快赢与精修项）

- **输入/鼠标编码零散分配**：每键/每事件多次小分配、常量序列反复 Encoding.GetBytes（[TerminalInputEncoder.cs:54-73](../src/SurgeLink.Terminal/TerminalInputEncoder.cs:54)、[TerminalMouseEncoder.cs:28-35](../src/SurgeLink.Terminal/TerminalMouseEncoder.cs:28)）→ 预生成常量 byte[]/池化。
- **UpdateEraseCell 每字形重写结构体**（[TerminalEmulator.cs:758-759](../src/SurgeLink.Terminal/TerminalEmulator.cs:758)）→ 移至 SGR 变更点。
- **AppendCombining 逐次 string.Concat**（[ScreenBuffer.cs:398-401](../src/SurgeLink.Terminal/ScreenBuffer.cs:398)）→ 组合标记密集文本 O(n²) 改线性。
- **CharacterWidth 双查表**（[CharacterWidth.cs:143-204](../src/SurgeLink.Terminal/CharacterWidth.cs:143)）→ 合并区间表。
- **选择/可见文本导出无预分配**（[TerminalEmulator.cs:299-340](../src/SurgeLink.Terminal/TerminalEmulator.cs:299)）。
- **交互认证/主机键提示同步阻塞 async**（[SshConnectionFactory.cs:243](../src/SurgeLink.Protocols.Ssh/Internal/SshConnectionFactory.cs:243)、[HostKeyVerifier.cs:129](../src/SurgeLink.Protocols.Ssh/HostKeyVerifier.cs:129)）：SSH.NET 同步事件所迫，建议标注保留。
- **每击键 Task.Run**（[SshTerminal.cs:184](../src/SurgeLink.Protocols.Ssh/SshTerminal.cs:184)）、**ps 解析器逐行 Split**（[PsParser.cs](../src/SurgeLink.Protocols.Ssh/Monitoring/PsParser.cs)）、**ScpMetadata 每条目子进程**（od/tr/stat/readlink，[ScpMetadata.cs:17-34](../src/SurgeLink.Protocols.Ssh/ScpMetadata.cs:17)）、**ps 变体试错未缓存**（[LinuxMonitorProbe.cs:188](../src/SurgeLink.Protocols.Ssh/Monitoring/LinuxMonitorProbe.cs:188)）。
- **Serial 逐块 ToArray + 每次写 Task.Run**（[SerialTerminal.cs:62](../src/SurgeLink.Protocols.Serial/SerialTerminal.cs:62)）、**SCP 取消断共享连接**（[ScpFileSystem.cs:296](../src/SurgeLink.Protocols.Ssh/ScpFileSystem.cs:296)）。
- **Agent 响应 JSON 多重序列化 + MCP 双份载荷**（[AgentContract.cs:15](../src/SurgeLink.Agent.Contracts/AgentContract.cs:15)、[AgentWire.cs:11-20](../src/SurgeLink.Agent.Contracts/AgentWire.cs:11)）。
- **采样器空闲期固定 1s 唤醒 + 心跳无条件触发变更事件**（[RemoteMonitorSampler.cs:57-61](../src/SurgeLink.Core/Monitoring/RemoteMonitorSampler.cs:57)、[AgentGateway.Clients.cs:40-43](../src/SurgeLink.Agent/AgentGateway.Clients.cs:40)）。
- **ZIP 下载 CompressionLevel.Optimal**（[FileTransferQueue.cs:468](../src/SurgeLink.Core/Files/FileTransferQueue.cs:468)）：单参数改 Fastest，CPU 不再是瓶颈，吞吐可提 2~4 倍。
- **App 侧零散**：FileLoggerProvider 每条日志同步开文件写（[FileLoggerProvider.cs:30](../src/SurgeLink.App/Services/FileLoggerProvider.cs:30)）、光标 530ms 闪烁 timer 无条件跑（[TerminalSurface.Cursor.cs](../src/SurgeLink.App/Views/Terminal/TerminalSurface.Cursor.cs)）、SettingsDialog slider 每 tick 整对话框重建、侧栏搜索每键新建防抖 timer、DialogAutomation.LayoutUpdated 全树递归找按钮、SFTP 剪切粘贴逐条目 ListAsync。

---

## 5. 优化路线图

### Phase 1 — 快赢（低风险，1~2 天量级）
1. ShellBufferSize 4KB → 32~64KB（配置级改动，高吞吐立竿见影）
2. ZIP 下载 CompressionLevel.Fastest（单参数）
3. 会话编辑凭据按对话框缓存（消除每键 DPAPI/文件读）
4. AgentPanel timer 生命周期修正（隐藏/未订阅即停）
5. PrivacyDisplay / WorkspaceSnapshot 派生值缓存
6. SFTP 选区恢复集合化（HashSet 代替 Contains/IndexOf）
7. 心跳事件仅在 peer 集合变化时触发；采样器空闲唤醒退避
8. 日志异步写、光标 timer 按可见性启停

### Phase 2 — 结构性（中风险，需补测试）
1. TerminalOutputBuffer 改分段环形缓冲；_output.Append 移出 SyncRoot
2. 远端目录列举缓存 + 单条目 stat；staging 操作批量化（消 O(N²)）
3. SCP 传输锁按方向/通道拆分 + 并发传输回归测试
4. 非 ASCII 单码点字符串缓存；Resize 移除/懒惰化全历史重排
5. 编辑器行索引增量更新 + 行号栏增量维护（消每键 O(n)）
6. Feed 锁外派发应答/事件 + 大块分片解析
7. AppSettingsStore 异步写队列（含关闭冲刷与崩溃一致性）
8. 设置/凭据 JSON 存储内存缓存 + 时间戳失效，DPAPI 移出锁

### Phase 3 — 深层改造（高收益，需设计评审）
1. AnsiParser ASCII 批量快路径（2~5× 解析吞吐）
2. RemoteReadStream 拷贝链消除 + channel 异步写
3. SSH 主机探测合并为单脚本单往返
4. Windows/Linux 监控脚本瘦身与间隔自适应
5. TelnetCodec span 化 + 读写锁分离
6. AgentGateway 串行粒度拆分（读写分离）+ 锁外回调
7. DesktopConnection 发现 TTL 缓存 + 事件驱动退出监视
8. workspace 变更 → 增量更新子树，废除整壳重渲染

---

## 6. 验证与度量建议

- **基准场景**：① 大输出（cat 100MB 文件 / 编译日志）终端吞吐与 GC；② 千条目目录批量上传/下载/删除；③ 4MB 文本编辑器连续输入与光标移动；④ 多终端（≥8）下 SFTP 导航/切 tab 帧率；⑤ 窗口拖拽 Resize；⑥ 高延迟（100ms RTT）链路会话就绪与传输。
- **工具**：dotnet-trace / PerfView 采样 + ETW GC 事件；GC.GetAllocatedBytesForCurrentThread 微基准（BenchmarkDotNet）；远端侧观察 powershell/子进程生成频率。
- **回归**：现有 1102 项测试全绿为基线；为环形缓冲游标语义、lstat 符号链接判定、编辑器换行边界、并发传输各补专项用例。
- **度量指标**：输出风暴 memmove 字节数、每键输入分配字节、每文件远端往返次数（应从 6~9 降至 ≤2）、UI 线程单帧最长阻塞、大目录操作网络字节数。

---

## 7. 保持的优点（勿在优化中破坏）

- AsciiText 单字符缓存使纯 ASCII 打印零分配；AnsiParser 参数数组复用
- 行引用移动 + 环形历史 + 行池；Invalidated 每 Feed 批量一次
- 渲染层行级 diff（只更新变化行）；监控采样器锁外通知、快照 + 节流发布
- 原子写 + 跨进程锁文件；帧上限与重放保护
- 文件操作的私有临时目录 + 提交前类型复核等安全语义（优化批量化时必须保留）
