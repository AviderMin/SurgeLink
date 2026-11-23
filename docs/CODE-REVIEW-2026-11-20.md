# 代码审查与改进报告 — 2026-11-20

审查对象：`master` 工作树（基线 `ea4935e`）。范围覆盖 `src/`、`tests/`、`tools/`、`.github/workflows/` 以及根目录文档。
本轮为静态审查 + 本地构建/测试验证，不是渗透测试；真实 SSH/串口设备与 ARM64/Windows 10 实机仍需按
`docs/VERIFICATION.md` 验证。

## 1. 结论摘要

- 基线构建 0 警告 0 错误；全量测试 **1517 项通过、0 失败**。
- 本轮确认并修复 16 类真实缺陷（其中 2 项高影响：终端历史阅读位置漂移、括号粘贴注入），
  并新增文档版本一致性 CI 校验。
- 最终构建 0 警告 0 错误；全量测试 **1545 项通过、0 失败**（新增 28 项回归用例）。

## 2. 已修复缺陷

### 正确性 / 用户可见

| 级别 | 位置 | 问题 | 修复 |
|---|---|---|---|
| 高 | `src/SurgeLink.Terminal/TerminalEmulator.cs:799`（`OnLineScrolledOff`） | 用户向上滚动阅读历史时，任何新输出行都会让视口内容整体下移一行；环形缓冲淘汰最老行时甚至下移两行。README 与 `docs/VERIFICATION.md` 明确承诺"新输出不会打断阅读"，现有 `LeadExperienceTests` 却把错误行为写成了断言 | 每新增一条历史行就把 `_viewportOffset` 加一（并夹取到历史长度），淘汰时不递减；同步修正两组断言 |
| 高 | `src/SurgeLink.Terminal/TerminalInputEncoder.cs:54`（`EncodePaste`） | 括号粘贴模式下载荷原样包裹，远程可控文本中的 `ESC[201~` 可提前结束粘贴帧，剩余内容被应用当作键盘输入执行（粘贴注入） | 括号粘贴时移除载荷中的 ESC 与 C1 CSI/OSC/ST 控制符；非括号粘贴保持原样（用户可能主动粘贴转义序列） |
| 中 | `src/SurgeLink.Terminal/TerminalEmulator.cs:1150`（模式 1049 退出） | 退出备用屏幕时只恢复光标行列，不恢复保存的 SGR 与 origin mode；退出 vim/less 后主屏幕继承应用的颜色和区域原点 | 恢复 `_alternateSavedCursor.Sgr` 与 `OriginMode` |
| 中 | `src/SurgeLink.App/Views/Terminal/TerminalSurface.Cursor.cs:24` | 光标用屏幕行号当视口行号绘制；向上翻看历史时光标画在错误的行上并覆盖无关历史文本 | 绘制前加 `emulator.ViewportOffset` 并据此取单元格 |
| 中 | `src/SurgeLink.Terminal/CellLine.cs`、`ScreenBuffer.cs`、`TerminalEmulator.cs:309` | 复制多行文本时对每个物理行都插入换行；软换行的长命令/日志被复制成多行，粘贴后会被拆成多条命令 | `CellLine.Wrapped` 记录软换行，`GetSelectionText` 在软换行处不插入 `\n` |
| 中 | `src/SurgeLink.Protocols.Ssh/ScpFileSystem.cs:203` | 递归删除根目录的保护只比较字符串 `"/"`；`//`、`/./`、`/..` 都能通过并发出 `rm -rf`（SFTP 后端已用词法归一化，SCP 后端遗漏） | 新增 `IsRootPath`（`PosixPath.Normalize`）并用于守卫；Agent Gateway 的同类检查同步加固 |
| 中 | `src/SurgeLink.Agent/AgentContract.cs:35`、`AgentGateway.cs:211`、`src/SurgeLink.App/Services/AgentFilePolicy.cs` | `files.upload/download` 直接使用本机任意路径，但宿主策略回调只拿到远端路径，桌面端策略恒真：被提示注入的 Agent 可上传 `%USERPROFILE%\.ssh` 私钥或覆盖启动目录文件 | `AgentFileAccess` 增加 `LocalPath`/`Destination`；桌面端对本地传输逐次弹出确认框并显示确切本地路径，无 UI 时默认拒绝；远程读写仍沿用会话授权 |
| 低 | `src/SurgeLink.Terminal/AnsiParser.cs:117` | OSC/DCS 字符串被 ESC 中止后，`ESC ESC` 的第二个 ESC 被吞掉，后续 `ESC[31m` 被当作普通文本打印 | `FeedEscape` 在 ESC 分支保持 `Escape` 状态 |
| 低 | `src/SurgeLink.Core/Sessions/SessionManager.cs:171` | 键盘交互/本地设备等不需要存储凭据的配置也会解密凭据库；DPAPI 调用不必要且可能暴露解密失败状态 | 仅在本次确实需要密码或私钥口令时才查询凭据库 |
| 低 | `src/SurgeLink.App/Views/Terminal/TerminalStyleResolver.cs:15` | 真彩色输出可产生数百万种颜色，画刷缓存无上限，长会话内存持续增长 | 缓存上限 4096，满后清空重建 |
| 中 | `src/SurgeLink.Core/Files/RemoteFileCopy.cs:21` | 文件夹复制中途失败会留下半成品目标目录（还停在 0700），且"目标已存在"检查让后续重试永远失败 | 记录本次创建的顶层目标，失败时递归删除后再抛出原异常 |
| 低 | `src/SurgeLink.Core/Files/RemoteFileWrite.cs:50` | `CreateDirectoryAsync` 在服务端成功但回包丢失时 `stagingStarted` 仍为 false，空的 `.surge-*.tmp` 目录永久残留 | 在 await 之前标记 staging 已开始，finally 始终尝试清理 |
| 中 | `src/SurgeLink.Core/Files/RemoteFileDelete.cs:20` | 只校验叶子名，`/home/u/../etc/passwd` 这类含 `..` 的路径能通过并删除用户未选中的目标 | 拒绝任何包含 `.`/`..` 路径段的 FullPath（与复制路径校验一致） |
| 低 | `src/SurgeLink.Protocols.Ssh/Monitoring/MemInfoParser.cs:44` | 不可信 `/proc/meminfo` 的 kB 值乘 1024 可溢出为负数，导致总量/交换空间显示错误 | 乘法前检查上限；SwapUsed 夹取到 `[0, SwapTotal]` |
| 低 | `src/SurgeLink.Protocols.Ssh/Monitoring/DfParser.cs:60` | `df:` 错误行或短行不会清空上一行保存的换行设备名，下一行真实记录被误解析后丢弃 | 跳过行时清空 `wrappedDevice` |
| 低 | `src/SurgeLink.Protocols.Ssh/Monitoring/WindowsDiskIoParser.cs:46` | `TryParseRate` 接受 `Infinity`，面板显示 `∞ B/s` | 拒绝非有限值 |

### 文档 / 工程

| 位置 | 问题 | 修复 |
|---|---|---|
| `README.md:7` | 首页写"当前版本 1.0.3.1"，与 `Directory.Build.props` 和同文件末尾的 1.0.3.6 矛盾 | 更正为 1.0.3.6 |
| `.github/workflows/ci.yml` | 版本号只有人工维护，容易再次漂移 | 新增 `tools/check-version.py` 并在 CI 中执行；`docs/VERIFICATION.md` 同步列出 |
| `.gitignore:105` | 末尾残留无效的 `%SystemDrive%/` 条目 | 删除 |
| `docs/AGENT-CLI.md`、`docs/AGENT-SECURITY-BOUNDARY.md` | 未描述本地文件访问的确认边界 | 补充逐次确认与默认拒绝说明 |

## 3. 验证

- `dotnet build SurgeLink.slnx -c Debug -p:Platform=x64` → **0 警告 0 错误**
- `dotnet test SurgeLink.slnx -c Debug -p:Platform=x64 -m:1` → **1545 通过 / 0 失败**
  （Agent 128、Core 435、Conformance 74、Serial 32、SSH 477、Telnet 17、Terminal 382；基线 1517）
- `python tools/check-repository.py` → 499 文件 0 发现
- `python tools/check-ui-localization.py` → 627 处调用 / 619 条翻译，0 发现
- `python tools/check-version.py` → 文档版本与 `Directory.Build.props` 一致
- `tools/verify-app.ps1`（Release 构建，真实 UI Automation 驱动，含中英文两阶段）→ **81 通过 / 0 失败**，无 Render error
- `git diff --check` → 通过

新增回归用例覆盖：滚动历史稳定性（含历史满时淘汰）、软换行复制、括号粘贴注入、备用屏幕 SGR/origin
mode 恢复、OSC 后双 ESC、SCP 根目录词法变体、Agent 本地路径传递与根目录递归删除变体、键盘交互不查询凭据库、
文件夹复制失败回滚、删除路径的 `..` 段拒绝，以及 `/proc`、`df`、Windows 磁盘解析器的异常数值输入。

## 4. 未修复 / 残余风险

以下项目本轮未改动，建议后续单独处理：

> 后续更新（2026-11-23）：第 2 项“重放缓存占用数百 MB”已过时。`95a2b3a` 已把
> `InputReplayCache` 限制为最多 1024 条且总序列化载荷不超过 16 MiB，并在
> `AgentTextTests` 中覆盖该上限；当前风险是每 grant 16 MiB 的硬上限，而不是无界增长。

1. **SCP 回退读取的 TOCTOU**：`ScpFileSystem.OpenReadAsync` 先用 `test` 类命令确认普通文件，再另起
   `cat --` 读取；远端可在两次往返之间把文件替换为 FIFO，使 `cat` 阻塞（`SshCommandRunner.CopyToAsync`
   未设命令超时）。修复需要把类型检查与读取合并为单条远端命令，并加首字节/空闲超时。
2. **`terminal.exec` 重放缓存**：每个 grant 最多保留 1024 条完整响应，单条输出上限约 512 KiB，
   极端情况下可占用数百 MB 桌面进程内存；建议只保留指纹与截断后的状态/摘要。
3. **SFTP 非覆盖提交依赖服务器语义**：SFTP v3 的 RENAME 在目标存在时"应当"失败，但并非所有服务器实现；
   当前依赖该行为保证 `overwrite=false`。需在真实服务器矩阵验证。
4. **终端逻辑行重排**：窗口缩窄再放宽不做 reflow，属于 `ITerminalEmulator.Resize` 既有契约；本次未改。
5. **App 层无自动化测试工程**：`TerminalSurface` 光标定位、`AgentFilePolicy` 弹窗等仍需真机 UI 验证；
   本轮已尽量把可测逻辑下沉到 Core/Terminal 并补测试。
6. **真实环境验证**：ARM64 与 Windows 10 21H2 实机、真实 SSH/SFTP/SCP 服务器、串口设备仍需按
   `docs/VERIFICATION.md` 执行。
