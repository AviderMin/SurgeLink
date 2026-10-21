# SurgeLink 黑盒测试包

基线：应用 1.0.2 / APPX 1.0.2.0；Windows 11 x64。

## 交付物

- `cases.json`：147 项结构化用例，ID 为追踪依据。
- `cases.csv`：可直接用 Excel 打开和分配执行的用例表。
- `../../docs/BLACKBOX-CASES.md`：中文用例清单。
- `../../docs/BLACKBOX-REVIEW.md`：项目测试审查、缺口、环境与验收说明。
- `../../tools/blackbox/Run-BlackBox.ps1`：统一批处理入口。
- `../../tools/blackbox/Test-Package.ps1`：18 项 ZIP / APPX / CER / 安装脚本检查。
- `../../tools/blackbox/Test-Desktop.ps1`：25 项 UI Automation 冒烟和表单边界检查。

这些脚本不引用应用源码、私有方法或应用内部模型，以发布文件、安装脚本返回值、操作系统 UI Automation 和可见界面为测试接口。原有 `dotnet test` 的单元测试不计入本黑盒通过数。

## 环境

统一入口使用 PowerShell 7：`pwsh`。安装脚本校验子进程使用系统 Windows PowerShell 5.1。UI 套件需要可交互、未锁屏的 Windows 桌面；不支持 Windows 服务/无桌面 CI 会话。

无需 Pester 或额外下载测试框架。不会自动编译、安装应用或导入证书。

## 安装包批量检查（默认，可直接执行）

在仓库根目录运行：

```powershell
pwsh -NoProfile -File tools/blackbox/Run-BlackBox.ps1 -Suite Package `
  -ZipPath artifacts/appx/SurgeLink_1.0.2_x64.zip -ExpectedVersion 1.0.2.0
```

只在本次报告目录下新建工作副本。篡改和缺失文件测试仅作用于解压后的副本，不修改原 ZIP、已安装应用、证书存储或用户配置。工作副本保留用于复核，每次约占一个安装包的展开空间。确认不再需要报告后，可手动清理对应运行目录。

`PKG012` 验证 CMS 密码学签名，`PKG013` 核对配套证书；这不等于 Windows 证书链信任或完整安装验证。`PKG007` 只检查合并索引存在且非空，不能替代安装后启动或资源内容验证。

## 桌面批量检查（专用账号/VM）

桌面脚本不改全局 settings.json，不输入密码，不连接真实 SSH，不安装/卸载应用。正常通过时只打开和取消对话框、检查表单输入、输入并清空筛选。若表单校验存在缺陷，点击“保存”可能留下 `blackbox.invalid` 的测试会话，因此必须在专用 Windows 测试账号/VM 执行。

```powershell
# 先在测试账户构建或准备好 EXE，关闭其他 SurgeLink 实例。
pwsh -NoProfile -File tools/blackbox/Run-BlackBox.ps1 -Suite Desktop `
  -ExecutablePath 'D:\SurgeLink-test\SurgeLink.exe' -ConfirmTestProfile

# 同时执行安装包和桌面套件
pwsh -NoProfile -File tools/blackbox/Run-BlackBox.ps1 -Suite All `
  -ZipPath artifacts/appx/SurgeLink_1.0.2_x64.zip `
  -ExecutablePath 'D:\SurgeLink-test\SurgeLink.exe' -ConfirmTestProfile
```

脚本如发现任何现有 SurgeLink 进程会拒绝启动；只清理自身创建的进程，不接管或终止用户实例。优先使用正常关闭，超过 3 秒才终止本脚本创建的进程。预期行为通过超时轮询判断，找不到或匹配多个控件均失败，不静默选第一个。

UI 套件支持中英文标签，不自动更改用户语言。设置/关于的保存语义、主题视觉效果、开始菜单激活、Demo 文件操作、真实 SSH/SFTP、凭据、安全、高对比度等在手工用例中列出，不能由本 UI 冒烟通过推断全部功能正确。

如需单独调整 UI 等待时间：

```powershell
pwsh -NoProfile -File tools/blackbox/Test-Desktop.ps1 `
  -ExecutablePath 'D:\SurgeLink-test\SurgeLink.exe' -ConfirmTestProfile -TimeoutSeconds 30
```

## 报告与退出码

默认每次创建唯一的 `artifacts/blackbox/run-日期时间-随机标识/`：

- `summary.json`：设计数、执行数、通过/失败/跳过/未执行数量。
- `coverage.csv`：147 项全量覆盖状态；手工和未选择的套件保持 `NotRun`。
- `package/`、`desktop/`：各套件的 `results.json`、`results.csv`、`report.html`、`junit.xml`。

状态：`Passed` 已断言通过；`Failed` 实际失败；`Skipped` 前置条件失败使该断言无法执行；`NotRun` 本轮没有执行。禁止将跳过或设计用例计入通过数。

退出码 0 表示所选自动套件成功；任一断言失败或基础设施错误返回非零。全部跳过不能算成功。JUnit 可接入 CI；GUI 套件只放在有交互桌面的专用 Windows runner。

```powershell
# 列出用例，不启动应用
pwsh -NoProfile -File tools/blackbox/Run-BlackBox.ps1 -ListCases
```

## 手工执行与测试数据

104 项手工用例必须逐项记录实际结果、执行人、日期、应用版本、Windows build、夹具版本及截图/日志证据。不要把设计用例文件里的 `NotRun` 批量改成 `Passed`。

CSV 已预留 ActualResult、ExecutedBy、ExecutedAt、Evidence、DefectId 空列，可直接记录执行结果与缺陷追踪。

- 用 VM 快照提供干净安装、旧版升级、无证书、UAC 拒绝等场景；保留已保存测试会话以验证升级兼容性。
- Demo 仅用于离线导航和文件行为，不证明真实 SSH、SFTP 或 SCP 可用。
- Linux：准备 OpenSSH、正常 SFTP、禁用 SFTP 的 SCP 回退服务、密码和私钥测试账户、只读目录。
- Windows：准备启用 OpenSSH 和可用 CIM 权限的测试账户。
- 网络错误：仅在隔离测试网络中模拟丢包、断开和端口拒绝。
- 传输文件：0、1、4095、4096、4097 字节，1 MiB 和 100 MiB；包含二进制、UTF-8 中文、空格路径；传输前后计算 SHA256。
- 每个远端用例只操作 `BB_<随机标识>` 独立目录；不得删除或改动夹具之外的数据。
- 密码、私钥口令不要写进仓库、报告、命令行参数或截图。使用专用测试账户，证据中遮盖敏感信息。

## 本次实际结果

安装包套件针对现有 `SurgeLink_1.0.2_x64.zip` 完整执行：18 通过、0 失败、0 跳过。桌面 25 项和手工 104 项尚未执行；仍需独立测试环境。没有为了生成这些报告改变用户配置或证书信任。
