# SurgeLink

<img src="docs/LOGO.png" width="128" height="128" alt="SurgeLink Logo" />

**把远程终端、文件管理和系统监控放进同一个 Windows 工作区。**

SurgeLink 是一款使用 C#、WinUI 3 和 [Microsoft.UI.Reactor](https://github.com/microsoft/microsoft-ui-reactor) 构建的开源 SSH／Telnet／串口客户端。通过标签页管理多个连接，在终端、远程文件和监控视图之间切换；无需服务器，也能用内置离线演示体验主要界面。

当前源码版本：**1.0.2.48**。安装包下载见 [GitHub Releases](https://github.com/AviderMin/SurgeLink/releases)。

[更新](#版本更新) · [功能](#功能) · [界面预览](#界面预览) · [开始使用](#开始使用) · [构建](#从源码构建) · [打包](#打包与-github-actions) · [Agent 与 CLI](#ai-agent-与-cli共享终端预览) · [文档](#开发文档)

![SurgeLink 终端与会话侧栏](docs/images/main-window.png)

## 版本更新

**1.0.2.48** 改进日常使用中的终端、编辑器与错误恢复体验：

- 终端向上回看日志或复制历史内容时，远端持续输出不再把视图拉回底部；在自己的终端执行后续命令前会重新回到实时输出。
- 内置文本编辑器正确处理 Windows（CRLF）换行：跨行查找、替换、光标与选区不再错位，行号栏和字符数按真实行数显示，未改动的换行不会被算作修改。
- 设置保存失败（目录只读、磁盘已满或文件被占用）时不再关闭设置窗口，所有修改保留并可重试，同时显示中英文错误说明。
- 连接非英语 Linux 主机时，监控页进程列表的 CPU、内存和排序恢复正常。
- 修正 SSH 命令输出编码：只在流开头存在当前编码的真实签名时剥离 BOM，Latin-1 文本与 UTF-16 内容中的 U+FEFF 不再被吞掉，Windows 监控采样首段不再丢失。
- Agent 读取超长终端画面时不再从中间截断 emoji；超过 1 MiB 传输上限时返回可操作的缩小请求提示。
- 修正中文宽字符后的制表位与经典鼠标协议的横向滚轮误报点击。

**1.0.2.45** 修复内置文本编辑器无法多行编辑：

- 修正受控 TextBox 的写入顺序导致每输入一个字符就把整篇文档压成一行的问题，换行符不再丢失，行号栏与内容同步。
- 编辑过程中保留光标位置，此前每次重渲染都会把光标移到文末。
- 查找替换、替换全部、行号计数与编辑框状态保持一致；行号栏滚动同步不再重复订阅。

**1.0.2.43** 完善 AI Agent 接入：

- Agent 面板默认提供通用 MCP JSON 配置，支持用户级配置跨工作区使用；单实例自动发现，桌面重启后无需修改配置，多实例可绑定目标桌面。
- 面板独立显示 MCP 客户端接入状态；连接客户端不会自动授权会话，未授权时会话列表为空。
- CLI 接入指令包含可执行文件绝对路径、实例 PID、会话核对与结果验证流程，并补充后台启动及输出捕获说明。

**1.0.2.42** 增加以下功能与修复：

- 文件支持多选删除；SCP 文件列表和 Stat 使用带完整性校验的元数据记录，完整保留含换行的文件身份。
- 共享监控采样器改为锁外通知，处理退订竞态，并在采样、进程查询和通知完成后等待配置间隔。
- 覆盖提交前重新检查目标类型，拒绝目录、符号链接和特殊文件；特殊文件和符号链接不能作为普通文件编辑或下载，内容覆盖不再自动继承特殊权限位。
- 断开连接后停止远程文件操作；编辑器支持复制全部文本、导出本地副本及恢复已加载的副本。
- SSH Shell 退出后提供重新打开入口，保留 SSH 连接、文件和监控；取消认证提示会立即终止认证。
- 终端支持鼠标事件与 SGR 1006 编码，按住 Shift 可进行本地选择和滚动；光标遵循远程程序请求的闪烁模式。

SCP 元数据与监控采样器的验证及兼容条件见 [二次审查修复验证](docs/VERIFICATION-SECOND-2026-10-25.md)。

**1.0.2.41** 在 1.0.2.40 基础上增加以下修复：

- 上传遇到同名目标时默认跳过，可选择重命名或明确覆盖；文件夹统一展示冲突，执行时再次拒绝未经授权的覆盖。
- 密码认证回退到键盘交互时自动回答明确的密码挑战；主机指纹和 MFA 提示的人工等待不计入网络建连超时，仍支持取消。
- SCP 列表优先使用完整 UTC 日期，旧式日期支持跨年回退；权限显示包含 setuid、setgid 和 sticky。
- Demo 执行 `exit` 后正确断开；终端支持闪烁文本；Windows 进程 CPU 回退按采样差值计算。
- 侧栏与监控页共享采样器，无订阅者或连接断开时停止采样。

本轮验证及仍需验收的 MFA 多连接限制见 [后续审查修复验证](docs/VERIFICATION-FOLLOWUP-2026-10-25.md)。

**1.0.2.40** 集中修复远程文件操作与终端的边界问题：

- 保留 POSIX 文件名中的反斜杠和首尾空格；复制支持同目录下仅大小写不同的文件，并保留文件与文件夹的普通权限。
- 上传和编辑保存先在私有临时目录中完成，避免受限文件的内容在写入过程中因默认权限而暴露；修正 SFTP 权限转换及 SCP 特殊权限位解析。
- SSH／SCP 回退读取改为可取消的流式传输，文本超限或用户取消时停止读取，无需先将整个远程文件下载到本地。
- 滚动历史行数设置对新建、重连及已打开的终端生效；修复背景色擦除，限制异常组合字符造成的资源消耗。
- 补齐 Inno 安装器的 Windows 文件版本元数据，与产品版本保持一致。

本轮新增 51 项回归用例，1102 项测试全部通过，并完成本机 SFTP 与回退读取的协议联调。验证范围和复现对比见 [审查修复验证](docs/VERIFICATION-REVIEW-2026-10-25.md)。

## 功能

| 功能 | 当前支持 |
| --- | --- |
| SSH 连接 | 密码、私钥及键盘交互认证；首次连接确认主机指纹，密钥变化时重新确认 |
| Telnet 连接 | 默认端口 23，在终端内交互登录；支持选项协商、终端类型及窗口尺寸同步，连接不加密 |
| 串口连接 | 本地 COM 端口，支持设备枚举与刷新、波特率、数据位、校验位、停止位、软／硬件流控及 DTR／RTS |
| 终端 | 自研 VT100/xterm 模拟器，支持 UTF-8、中日韩宽字符、16／256／真彩色、可配置的滚动历史、文本选择与复制粘贴 |
| 远程文件 | SFTP 浏览与上传下载；服务器没有 SFTP 子系统时回退到 SSH 命令与 SCP |
| 文件操作 | 新建文件与文件夹、编辑文本、重命名、删除、权限编辑；右键剪切、复制、粘贴及查看属性 |
| 传输管理 | 会话内传输队列、进度与取消；切换到终端或监控视图后仍保留队列 |
| 系统监控 | CPU、内存、交换空间、磁盘、网络和进程；支持 Linux 与 Windows OpenSSH 主机，具体指标取决于服务器能力 |
| 会话管理 | 分组、筛选、多标签页，以及编辑、复制和删除保存的连接 |
| 系统图标 | 连接后识别系统并保存图标信息，内置 Windows、macOS、常见 Linux 发行版和 FreeBSD 图标 |
| Agent 与 CLI | 按会话授权读取或控制共享终端，提供 MCP stdio Host 与 CLI；人工输入可接管并撤销控制 |
| 界面 | 简体中文／English／跟随系统，浅色、深色及高对比度主题 |
| 离线演示 | 模拟终端、内存文件系统和合成监控指标，无需连接外部服务器 |

当前实现的协议是 **SSH**、**Telnet**、**Serial** 和 **Demo**。Telnet 与 Serial 仅提供终端；远程文件和系统监控需要 SSH。RDP、VNC 等尚未实现。

## 界面预览

截图来自当前源码构建的中文界面，使用离线 Demo 会话；其中主机、文件和监控指标均为演示数据。

### 远程文件管理

![远程文件管理](docs/images/files-view.png)

剪切、复制和删除支持多选，粘贴目标为当前远程目录。多选后可通过工具栏、右键菜单或 Delete 键批量删除，统一确认目标数量及路径；部分失败时显示结果并刷新目录。文件夹支持递归复制和删除；同名复制目标会提示冲突，复制时不跟随符号链接。Ctrl+X、Ctrl+C、Ctrl+V 可在当前文件视图中使用。

上传和文本保存先写入目标目录下的私有临时目录，成功后再提交；POSIX 临时目录在写入内容前限制为仅当前用户可访问。远程复制保留普通权限位，不传播 setuid／setgid 等特殊位。下载拒绝不安全的 Windows 文件名及覆盖已有本地文件。编辑器对实际读取内容限制为 4 MiB，关闭或重新加载前会提示未保存的更改。

文件视图会建立独立 SSH 连接，因此使用 MFA 的服务器可能再次要求验证码；不会缓存或重放一次性验证码。“登录一次即可使用整个工作区”仍需在目标 MFA 服务器上验收。

覆盖已有远程文件需要服务器支持安全替换：SFTP 使用 OpenSSH POSIX rename 扩展；SCP 回退需要 `mv/ln -T`，新文件提交还需要同文件系统硬链接。回退读取通过可取消的 `cat --` 命令通道流式传输，上传使用 SCP。不支持时操作失败并保留原目标，不降级为直接截断写入。

当前工作区的 SCP 元数据回退要求服务端提供 `sh`、支持 `-c` 格式的 `stat`、`od`、`tr`；读取符号链接还需 `readlink -n`。文件路径按 UTF-8 无损解码，所有者显示数值 UID/GID。工具不兼容、编码无效或响应不完整时拒绝列举，不再从普通 `ls` 文本推断可操作路径。

### 系统监控

![远程系统监控](docs/images/monitor-view.png)

### 新建会话

![新建会话表单](docs/images/new-session.png)

连接信息、身份验证和会话整理分别分组；表单内容可滚动，底部保留“保存并连接”“保存”和“取消”。

## 开始使用

### 系统要求

- Windows 10 21H2（内部版本 19044）或更新版本。
- 支持构建 **x64**、**ARM64** 安装包，请按设备架构选择已发布的安装包。
- 分发包包含 .NET 与 Windows App SDK 运行库，无需安装 .NET SDK。

1.0.2.15 起主线的安装与应用最低版本统一为 19044；Windows 10 21H2 实机运行尚待验证，ARM64 也需对应设备验证。

### 获取安装包

从 [GitHub Releases](https://github.com/AviderMin/SurgeLink/releases) 下载对应架构的 `SurgeLink-Setup-<版本>-<架构>.exe`，双击安装。安装程序包含运行库，支持开始菜单入口、可选桌面快捷方式和卸载，目前未进行代码签名。

需要 APPX 侧载时，下载 `SurgeLink_<版本>_<架构>.zip`，按下述步骤安装。单独打包的测试产物也可从 [GitHub Actions](https://github.com/AviderMin/SurgeLink/actions) 下载，通常需要登录 GitHub，保留 14 天。

完整解压一次后，目录中包含：

```text
SurgeLink_<包版本>_<架构>.appx
SurgeLink-Development.cer
Install.ps1
```

在解压目录执行：

```powershell
powershell -NoProfile -File .\Install.ps1
```

这是开发证书签名的侧载包。安装脚本校验文件与证书，必要时通过 UAC 请求将配套公开证书加入 TrustedPeople，再安装 APPX。它不是 Microsoft Store 的安装流程。只检查文件而不安装可使用：

```powershell
powershell -NoProfile -File .\Install.ps1 -ValidateOnly
```

详细说明见 [打包与 Microsoft Store 提交](packaging/README.md)。

### 创建连接

1. 点击“新建会话”，选择协议，填写主机、端口和用户名。
2. 选择密码、私钥或键盘交互认证方式；名称与分组可选。
3. 点击“保存并连接”，核对远程主机指纹后继续。
4. 在标签页内切换“终端”“文件”“监控”。

第一次成功获取系统信息后，会话侧栏显示对应图标，并在重启后保留。尚未识别的系统使用通用图标；修改主机、端口或协议会清除旧的识别信息。

没有可连接的服务器时，点击“打开演示会话”即可体验。

连接 Telnet 时，在“新建会话”中选择 **Telnet**，填写主机，端口留空使用 23；用户名仅作会话标记，可留空。保存并连接后，在终端按服务器提示输入用户名和密码。Telnet 不加密，也不验证主机身份；应用不会自动发送保存的凭据。终端使用 UTF-8，支持 BINARY、ECHO、SUPPRESS-GO-AHEAD、TERMINAL-TYPE 和 NAWS 协商。

连接串口时，选择 **Serial**，从检测到的设备列表选择端口，或手动输入 `COM3` 这样的名称；插入 USB 串口适配器后可刷新列表。默认 **115200、8 数据位、无校验、1 停止位、无流控**，DTR／RTS 默认关闭；请按设备要求调整。参数随会话保存，编辑和复制会保留参数。串口不使用网络端口、用户名或保存的密码，登录信息在设备终端内输入。字节按原样收发，文本终端使用 UTF-8；改变窗口尺寸只影响本地显示，不向设备注入控制命令。

## 从源码构建

开发需要 Windows、**.NET 10 SDK** 和匹配的 Windows App SDK 开发依赖；普通开发构建还需要相应的 Windows App Runtime。依赖版本由 `Directory.Packages.props` 统一管理。

```powershell
git clone https://github.com/AviderMin/SurgeLink.git
cd SurgeLink

dotnet build SurgeLink.slnx -c Debug -p:Platform=x64
.\src\SurgeLink.App\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\SurgeLink.exe
```

执行单元测试与协议契约测试：

```powershell
dotnet test SurgeLink.slnx -c Release -p:Platform=x64
```

测试覆盖终端解析、会话与存储、凭据保护、SSH 解析及协议能力契约。测试数量以实际运行结果为准。

## 打包与 GitHub Actions

本地生成包含运行库的安装包；EXE 打包需要 Inno Setup 6.3 或更新版本。脚本自动查找编译器，也可通过 `-IsccPath` 指定 `ISCC.exe`：

```powershell
pwsh -NoProfile -File tools/package-exe.ps1 -Version 1.0.2.48 -Architecture x64
pwsh -NoProfile -File tools/package-exe.ps1 -Version 1.0.2.48 -Architecture arm64
pwsh -NoProfile -File tools/package-appx.ps1 -Version 1.0.2.48 -Architecture x64
pwsh -NoProfile -File tools/package-appx.ps1 -Version 1.0.2.48 -Architecture arm64
```

| 产物 | x64 输出目录 | ARM64 输出目录 |
| --- | --- | --- |
| Inno EXE 安装包与 SHA256 | `artifacts/installer/x64/` | `artifacts/installer/arm64/` |
| APPX 分发 ZIP | `artifacts/appx/` | `artifacts/appx/arm64/` |

例如，x64 Inno 安装包输出为 `artifacts/installer/x64/SurgeLink-Setup-1.0.2.48-x64.exe`，配套校验文件为同名的 `.exe.sha256`。

[Release 工作流](.github/workflows/release.yml) 支持手动运行或推送 `v1.0.2.48` 形式的数字版本标签。它复用 [Installer](.github/workflows/package-exe.yml) 和 [APPX](.github/workflows/package-appx.yml) 工作流，先运行测试，再分别构建两个架构。完整产物上传草稿后，核对或创建指向构建提交的 Tag，再公开 Release，提供 EXE、APPX ZIP 与 SHA256 校验清单。单独运行打包工作流只上传 Actions 产物，保留 14 天，不创建 Release。工作流不自动提交 Microsoft Store。

APPX 打包使用临时生成的开发签名密钥，完成后删除临时 PFX；不需要向仓库提交私钥或配置签名 Secrets。Inno EXE 当前未签名。ARM64 包在 x64 runner 上交叉编译，实际启动与功能仍需在 ARM64 设备验证。

## 数据与隐私

连接资料、设置、主机指纹和日志存放于 `%LOCALAPPDATA%\SurgeLink`；打包安装后 Windows 可能重定向此路径。

- 记住的密码和私钥口令通过当前 Windows 用户的 DPAPI 加密保存。
- 会话资料保存主机、用户名和私钥路径；私钥文件仍保留在用户选择的位置。
- 日志和崩溃记录保存在本地，可能包含主机名、用户名、路径及错误详情；分享前请检查并脱敏。
- 应用未内置广告、分析或自动诊断上传服务。
- 设置中的“隐私模式”可遮蔽会话列表、标签、连接信息和提示中的服务器 IP／域名；鼠标悬停时查看原文，移开后重新遮蔽。设置会保存，终端输出和远程文件内容不做替换。

完整说明见 [隐私政策](PRIVACY.md) 和 [安全说明](SECURITY.md)。

## 项目结构

```text
src/
  SurgeLink.App/                   WinUI 3 + Reactor 界面与应用服务
  SurgeLink.Core/                  会话、存储、安全及协议能力接口
  SurgeLink.Terminal/              VT/xterm 终端模拟引擎
  SurgeLink.Protocols.Ssh/         SSH、SFTP、SCP 与系统监控
  SurgeLink.Protocols.Telnet/      Telnet 终端与协议协商
  SurgeLink.Protocols.Serial/      本地串口终端与连接参数
  SurgeLink.Protocols.Demo/        离线演示协议
  SurgeLink.Agent/                 已授权连接的本地接入与共享终端控制
  SurgeLink.Cli/                   surgelink-cli 命令行客户端
  SurgeLink.AgentHost/             MCP stdio Host
tests/                            单元测试与协议契约测试
tools/                            构建、打包和开发辅助脚本
packaging/                        APPX 清单与安装模板
docs/                             架构、开发指南与界面截图
.github/workflows/                GitHub Actions 工作流
```

UI 使用 C# 元素树与 Reactor hooks 描述，底层仍是原生 WinUI 控件。协议通过能力接口向界面提供终端、文件和监控功能，新增协议不必重写应用外壳。

## AI Agent 与 CLI（共享终端预览）

点击顶栏四个操作按钮左侧的机器人图标，在 Agent 面板内为已打开会话选择“允许读取”或“控制终端”。
读取包括主机信息、终端屏幕与增量输出、监控、目录和文本文件；终端控制可以输入文本、发送 Enter／Tab／Ctrl+C 等按键，
并切换已授权会话的终端／文件／监控视图。Agent 操作使用用户正在看的同一个交互终端，输入与输出直接可见。
手动输入即可接管，面板提供逐会话停止、全部停止、MCP／CLI 接入配置和最近活动。
已允许终端控制的会话在标签标题右侧显示机器人图标，悬停提示“Agent 已接管此会话”；接管或撤销后自动移除。
撤销、关闭或重连后旧句柄失效；授权范围为同一 Windows 用户，当前不区分具体 Agent 应用。

提供 `surgelink-cli` 和独立的 MCP stdio Host，两者共用同一套接入与授权逻辑，不保存额外的 SSH 凭据。
发行打包脚本将它们分别放入 `cli/` 和 `agent/`；开发构建与 MCP 客户端配置见 [接入文档](docs/AGENT-CLI.md)。
推荐在 Agent 面板选择客户端，通过 MCP 一次注册到用户级配置，跨工作区使用；支持 Codex、Claude Code、Cursor 及通用 MCP 配置引导。
默认自动发现唯一运行的 SurgeLink，桌面重启后无需修改配置，重新授权会话即可；多实例时可选择绑定当前桌面。面板单独显示 MCP 客户端接入状态，接入本身不会授权会话。
点击“复制 Agent 接入指令”，粘贴到外部 Agent 对话并补充需求，即可提供包含 CLI 绝对路径、当前实例 PID 和完整操作流程的说明。
具备本地命令执行能力的 Agent 可从这台电脑同一 Windows 账户下的任意工作区调用，无需切换目录或配置 PATH；SurgeLink 重启后请重新复制。
每次新的终端输入前，Agent 必须先通过 Check Sessions 核对目标连接和当前屏幕，再使用返回的校验令牌与输入修订号发送输入；具体调用方式见接入文档。
执行策略由外部 Agent 应用负责，授权后 SurgeLink 不逐条审批命令或文件读取。输入回执只表示发送结果，不表示命令已完成或成功。
终端命令具有当前远端账户的权限；专用文件修改 API、逐客户端配对和独立 exec 退出码尚未提供。
终端和文件内容可能包含敏感信息，外部 Agent 可能将读取结果发送给其模型服务商；界面隐私遮蔽不会替换接入返回内容。

## 开发文档

- [Agent CLI 与 MCP 接入契约](docs/AGENT-CLI.md)（会话授权、读取与终端控制）
- [外部 AI Agent 接入开发方案](docs/AI-AGENT-DEVELOPMENT.md)（设计评审与后续路线）
- [架构与线程模型](docs/ARCHITECTURE.md)
- [新增协议与契约测试](docs/ADDING-A-PROTOCOL.md)
- [WinUI 3／Reactor 界面规范](docs/UI-GUIDELINES.md)
- [开发验证说明](docs/VERIFICATION.md)
- [1.0.2.40 审查修复与验证](docs/VERIFICATION-REVIEW-2026-10-25.md)
- [打包与商店提交](packaging/README.md)

欢迎通过 [Issues](https://github.com/AviderMin/SurgeLink/issues) 提交问题或功能建议。报告问题时请附上版本、系统架构、复现步骤和经过脱敏的诊断信息。

## 许可证

项目采用 [MIT License](LICENSE)。内置系统图标的来源、署名与独立许可见 [图标声明](src/SurgeLink.App/Assets/SystemIcons/NOTICE.md)，其他依赖遵循各自的许可条款。
