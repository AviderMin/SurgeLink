# SurgeLink 性能优化总结

> 2026-11-01 · 基于四路并行静态代码审查（终端 / 协议 / UI / Core+Agent，覆盖 src 全部约 26,500 行 C#）
> 共记录 **54 条**性能发现（高 10 · 中 22 · 低 22）。完整证据（代码片段 + 改造伪代码）见 [artifacts/perf-review/](../artifacts/perf-review/) 四份详细文档与[完整报告](PERFORMANCE-OPTIMIZATION-REPORT.md)。

## 一句话结论

架构设计整体良好（行池/环形历史、渲染行级 diff、锁外通知、原子写等应予保持）；性能短板集中在 **远端元数据往返放大、UI 线程与锁内同步 IO、热路径分配拷贝、渲染放大** 四条主线上，均为"单点局部合理、规模放大后失效"的类型。

## 四条主线

| 主线 | 表现 | 涉及模块 |
|---|---|---|
| A. 远端元数据往返放大 | 每文件全量列举父目录 / 单条目 stat 拉全目录；每文件 6~9 个固定元数据往返 | Core、Protocols.Ssh |
| B. UI 线程与锁内同步 IO | 设置/凭据/DPAPI 同步读写、输出缓冲锁内搬移、锁内派发事件与依赖属性 | App、Core、Terminal |
| C. 热路径分配与拷贝 | 读循环逐块 new byte[]、非 ASCII 每字形字符串驻留、Resize 全历史重分配 | Protocols.Ssh、Terminal |
| D. 渲染放大 | 整壳重渲染、终端强制全量重绘、编辑器每键 O(文档)、每渲染 O(n·m) 恢复选区 | App |

## 高严重度（10 条，P0 优先处理）

| # | 问题 | 位置 | 影响与建议 |
|---|---|---|---|
| 1 | 远端逐项全量列举父目录，O(N²) 流量 | RemoteFileCopy.cs:35、RemoteFileWrite.cs:41、SftpFileSystem.cs:495 | 千条目目录批量操作网络量放大 1~2 个数量级，高延迟链路吞吐被 RTT 支配；→ 目录列举缓存 + 单条目 stat + staging 批量化 |
| 2 | TerminalOutputBuffer 满容量后每 4KB 锁内搬移 ~256KB | TerminalOutputBuffer.cs:28 | 输出风暴下 ~600MB/s 无谓 memmove，与 UI Paint 竞争同一把锁导致掉帧；→ 分段环形缓冲 + Append 移出 SyncRoot |
| 3 | SCP fallback 单锁串行化所有传输 | ScpFileSystem.cs:24 | 下载/上传互相阻塞，吞吐限为单流；→ 按方向/通道拆锁 + 并发传输测试 |
| 4 | SSH 读循环每 4KB 一次 new byte[] + BlockCopy | SshTerminal.cs:327 | 高吞吐 GC 压力大；→ 默认缓冲升 32~64KB + chunk 池化 |
| 5 | Resize 逐行重分配全部 scrollback（~60MB/次） | TerminalEmulator.cs:173 | 拖拽窗口每帧触发卡顿；读路径已有钳制，该循环不必要；→ 移除/懒惰化 |
| 6 | 非 ASCII 每字形 rune.ToString() 驻留历史 | TerminalEmulator.cs:357 | 全 CJK 历史仅字符串 ~100MB；→ 单码点字符串缓存复用，分配降 90%+ |
| 7 | 设置更新 UI 线程同步读写 JSON | AppSettings.cs:109 | 字体缩放每档一次磁盘往返 + 全订阅者重渲染；→ 异步写队列 + 关闭冲刷 |
| 8 | 会话编辑每键同步读凭据 + DPAPI 解密 | SessionEditorDialog.cs:342 | 打字每键数 ms~数十 ms 延迟；→ 按对话框缓存凭据快照 |
| 9 | 远程编辑器每键 O(文档)：全文拷贝 + 行索引重建 | FileEditorSurface.cs:56 | 4MB 文件连续输入线性恶化；→ 行索引增量更新 + 二分复用 |
| 10 | SFTP 单条目 stat 拉全目录 READDIR（并入 #1 处理） | SftpFileSystem.cs:495 | 大目录下 Stat/OpenRead/Delete 每步 O(n) 放大 |

## 中严重度（22 条，P1）

- **编辑器保存双读全文件 + 双哈希**（RemoteTextEdit.cs:16）：保存 = 下载×2 + 上传×1，大文件延迟 ×3 → 写后校验降级为 Stat 比对
- **JSON 存储每次查找全量重读 + 锁内 DPAPI/锁文件自旋**（EncryptedCredentialStore.cs:44）：并发连接互相阻塞 → 内存缓存 + 时间戳失效
- **AgentGateway 锁内调 UI 回调 + 每会话读写全串行**（AgentGateway.cs:128）：慢回调卡住网关，MCP 并发读被阻塞 → 锁外回调 + 读写分离
- **RemoteReadStream 拷贝链 + 同步阻塞 channel 写**（RemoteReadStream.cs:118）：GB 级下载每字节 2-3 次拷贝
- **SCP 上传先完整落本地临时文件**（ScpFileSystem.cs:144）：双倍磁盘 IO、无传输中进度
- **主机探测串行 5-7 条远程命令**（SshSession.cs:330）：高延迟链路会话就绪慢 1 秒+ → 合并单脚本单往返
- **Windows 监控每 2 秒拉起 powershell + 8 组 CIM 查询**（WindowsMonitorScript.cs:85）：监控自身成远端负载源
- **Feed 持锁解析整块数据、锁内派发事件**（TerminalEmulator.cs:133）：应答同步写通道即锁跨 IO → 锁外派发 + 分片解析
- **AnsiParser 无 ASCII 批量快路径**（AnsiParser.cs:60）：MB 级纯 ASCII 流量吞吐受限 → Ground 态批处理（2~5×）
- **workspace 变更触发整壳重渲染 + 终端强制全量重绘**（TerminalView.cs:46）：多终端场景每次事件数万格快照
- **Paint() 锁内设置 ScrollBar 依赖属性**（TerminalSurface.Paint.cs:40）：DP 回调放大 feed 线程阻塞窗口
- **SFTP 选区恢复 O(rows×selected) WinRT 跨界访问**（SftpView.cs:701）：大目录交互迟钝 → 集合化
- **WorkspaceSnapshot 派生数据每渲染重算**（WorkspaceStore.cs:88）、**PrivacyDisplay.Mask 每渲染重建正则管道**（PrivacyDisplay.cs:13）→ 缓存派生值
- **AgentPanel 常驻 5s timer 隐藏时仍全量重渲染**（AgentPanel.cs:33）→ timer 按可见性启停
- **TelnetCodec 逐字节 List + ToArray**（TelnetCodec.cs:53）、**TelnetTerminal 读写共用锁**（TelnetTerminal.cs:59）→ span 化 + 锁分离
- **SshCommandRunner 输出 ToArray 整份拷贝**（SshCommandRunner.cs:105）→ GetBuffer 等价替换
- **DesktopConnection 每请求进程枚举 + 250ms 轮询**（DesktopConnection.cs:27）→ TTL 缓存 + 事件驱动
- **LiteralTextSearch 反向查找最坏 O(n×m)**（LiteralTextSearch.cs:16）→ 切片 LastIndexOf 向量化

## 低严重度（22 条，P2 快赢与精修）

- **单参数快赢**：ZIP 下载 CompressionLevel.Optimal → Fastest（吞吐 2~4×）；ShellBufferSize 调大（见高 #4）
- **Terminal**：输入/鼠标编码每事件小分配（预生成常量 byte[]）、UpdateEraseCell 每字形重写（移至 SGR 变更点）、AppendCombining 逐次 string.Concat、CharacterWidth 双查表（合并区间表）、文本导出无预分配
- **Protocols**：每击键 Task.Run、ps 解析逐行 Split、ScpMetadata 每条目子进程（od/tr/stat/readlink）、ps 变体试错未缓存、Serial 逐块 ToArray、SCP 取消断共享连接、交互认证同步阻塞（SSH.NET 所迫，标注保留）
- **Core/Agent**：Agent 响应 JSON 多重序列化 + MCP 双份载荷、采样器空闲 1s 固定唤醒、心跳无条件触发变更事件
- **App**：日志同步开文件写、光标 530ms timer 无条件跑、SettingsDialog slider 每 tick 整对话框重建、搜索防抖每键新建 timer、DialogAutomation.LayoutUpdated 全树递归、剪切粘贴逐条目 ListAsync

## 优化路线图

**Phase 1 快赢**（低风险，1~2 天）：缓冲调大 → ZIP Fastest → 凭据缓存 → timer 生命周期 → 派生值缓存 → 选区集合化 → 心跳条件触发 → 日志异步写
**Phase 2 结构性**（中风险，补测试）：环形缓冲 → 目录缓存+stat → SCP 锁拆分 → 单码点字符串缓存 + Resize 懒惰化 → 编辑器行索引增量 → Feed 锁外派发 → 设置异步写队列 → JSON 存储缓存
**Phase 3 深层改造**（高收益，需评审）：AnsiParser 批量快路径 → RemoteReadStream 拷贝链 → 探测合并单往返 → 监控脚本瘦身 → Telnet span 化 → 网关读写分离 → DesktopConnection 事件驱动 → 整壳重渲染改增量

## 验证与度量

- **基准场景**：100MB 大输出终端吞吐/GC · 千条目目录批量传输 · 4MB 编辑器连续输入 · 多终端（≥8）导航帧率 · 拖拽 Resize · 100ms RTT 高延迟链路
- **工具**：dotnet-trace/PerfView + ETW GC；BenchmarkDotNet 微基准；远端观察子进程生成频率
- **回归**：1102 项现有测试为基线；为环形缓冲游标、lstat 符号链接、编辑器换行边界、并发传输补专项用例
- **指标**：输出风暴 memmove 字节数 · 每键输入分配字节 · 每文件远端往返（6~9 → ≤2）· UI 单帧最长阻塞 · 大目录操作网络字节数

## 优化时必须保持的优点

AsciiText 单字符缓存零分配 ASCII 路径 · AnsiParser 参数数组复用 · 行引用移动 + 环形历史 + 行池 · Invalidated 每 Feed 批量一次 · 渲染行级 diff · 采样器锁外通知与快照节流 · 原子写 + 跨进程锁文件 · 帧上限与重放保护 · 私有临时目录 + 提交前类型复核等安全语义

## 详细文档

- [完整报告 PERFORMANCE-OPTIMIZATION-REPORT.md](PERFORMANCE-OPTIMIZATION-REPORT.md)（P0 逐条展开 + P1/P2 全表）
- [terminal.md](../artifacts/perf-review/terminal.md)（10 条）· [protocols.md](../artifacts/perf-review/protocols.md)（17 条）· [app.md](../artifacts/perf-review/app.md)（16 条）· [core.md](../artifacts/perf-review/core.md)（11 条）
