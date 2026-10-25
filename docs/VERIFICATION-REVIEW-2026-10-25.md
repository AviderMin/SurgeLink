# 2026-10-25 审查修复验证

本次修复覆盖审查提出的九项问题，并修正协议联调发现的 SFTP 权限表示转换错误。
所有变更均在当前工作区，未修改用户的连接、凭据或实际远程文件。

最终验证：

- `dotnet test SurgeLink.slnx -c Release -p:Platform=x64 -m:1 --no-restore --nologo -warnaserror`：1102 项通过，0 失败，0 跳过；比审查基线新增 51 项回归用例。
- `dotnet build src/SurgeLink.App -c Release -p:Platform=x64 --no-restore -warnaserror --nologo`：成功，0 警告、0 错误。
- 仓库检查、本地化检查和 `git diff --check` 均通过。
- 本机真实 SSH.NET 协议联调全部通过；限额中止、运行中取消和取消后的下一次读取都已验证。

| 审查问题 | 修复行为 | 回归证据 |
| --- | --- | --- |
| POSIX 路径被改写 | 文件操作保留反斜杠及首尾空格，符号链接后的点段由服务器解析；拒绝 NUL | PosixPathTests、ScpFileSystemTests；本机 SFTP 读删验证 |
| 复制放宽权限 | 文件和目录保留普通权限，不传播特殊位；未知文件权限使用 0600 | RemoteFileCopyTests；本机 SFTP 0600 文件复制与只读目录复制 |
| 临时文件权限窗口 | 在目标父目录下创建独立暂存目录，限制为 0700 后才选取随机载荷名并写入；原子提交后清理 | StagedFileWriteTests；服务端审计确认全部载荷写入发生于 0700 目录内 |
| 组合字符资源放大 | 单个单元格最多保留 64 个 UTF-16 代码单元，超过时忽略后续组合标记，后续正常字符继续显示 | ReviewRegressionTests 的跨块、宽字符及累计分配测试 |
| SCP 读取限额与取消 | 回退下载通过可取消的原始 `cat --` 通道读取；适配器只排队两个 32 KiB 数据块，无完整本地预下载；SCP 上传保持不变 | RemoteReadStreamTests；本机协议限额、取消与后续读取验证 |
| SCP 大写 S/T 权限解析 | 大写特殊位不再同时设置执行位 | ScpFileSystemTests 的四种特殊权限输入 |
| 大小写误判冲突 | 远程预检采用精确名称比较，最终冲突由服务器原子提交判断 | RemoteFileCopyTests；本机 SFTP README/readme 同目录复制 |
| 历史行数设置无效 | 新建、重连及已打开终端都应用设置；缩小容量保留最新历史并约束视口；关闭时解除订阅 | TerminalBridgeResponseTests、WorkspaceRegressionTests、ReviewRegressionTests |
| 擦除背景色丢失 | 擦除、插入、删除和滚动产生的空白采用当前颜色；复位后恢复默认颜色 | ReviewRegressionTests 的 12 种操作及颜色恢复测试 |

联调额外发现，SSH.NET 的 `ChangePermissions` 接收八进制数字形式，例如 700，
项目接口则使用位掩码，例如 0x1C0。现在在 SFTP 边界进行转换，并通过
`SftpPermissionsTests` 调用实际 SDK 的 `SetPermissions` 验证全部普通与特殊权限位。

同步 SCP 下载在断开连接后可能仍阻塞在 SDK 内部管道读取。回退读取因此使用具有独立
生命周期的 SSH 命令通道，取消和失败时完成并释放其输出管道；此路径没有复用会卡住的
`ScpClient.Download`。上传仍使用 SCP，并在真正获得传输锁之后解析连接，避免使用之前
被取消的传输所断开的客户端。SDK 相关生命周期行为可参见
[SshCommand 源码](https://github.com/sshnet/SSH.NET/blob/2026.0.0/src/Renci.SshNet/SshCommand.cs)。

复现程序修复前后对比：

| 项目 | 修复前 | 修复后 |
| --- | --- | --- |
| `/data/..\victim.txt` | 变成 `/victim.txt` | 保留原路径 |
| `rwSr--r--` | 4744 | 4644 |
| 60,001 字节组合字符输入累计托管分配 | 901,560,040 字节 | 725,776 字节 |
| README/readme 目录复制 | 失败且只留下 README | 两者均复制 |
| 0600 文件的副本 | 0644 | 0600 |
| 受限内容暂存 | 先写内容，再 chmod | 先限制目录为 0700，再写内容 |
| 红色背景擦除 | 默认背景 | 保留红色背景 |
| 设置 1000 行后写入 1500 行 | 上限 10000，保留 1469 | 上限及保留数量均为 1000 |

分配量是测试线程的累计托管分配，不是进程峰值内存。两个排队块的限制约束应用适配器，
不能视为 SSH.NET 接收缓冲或整个进程的硬内存上限。过长的组合序列会被截断，这是明确的
资源保护策略。

本机协议测试使用仅监听 127.0.0.1 的临时 SSH 服务与隔离内存文件系统，调用实际的
SSH.NET SFTP 和回退读取实现。它验证了真实协议传输，但不能替代多用户 POSIX 权限、
Windows OpenSSH、真实串口硬件、ARM64 与完整 WinUI 交互验收。临时服务在测试后关闭。

POSIX 暂存写入现在需要服务器允许创建目录并限制其权限；无法建立私有暂存区时，在
写入内容前失败。新文件仍要求不覆盖提交，覆盖仍要求安全原子替换；没有降低为截断写入。
