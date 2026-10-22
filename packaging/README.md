# APPX 与 ZIP 发布

## GitHub Actions 多架构打包

工作流为 `.github/workflows/package.yml`，当前项目基于 WinUI，仅支持 Windows。
矩阵在 Windows x64 runner 上分别构建 x64、ARM64；ARM64 使用交叉编译。

- 在 GitHub 的 Actions → Package Windows → Run workflow 手动触发；版本留空时读取
  `Directory.Build.props`，也可输入 `1.0.2` 或四段数字版本。
- 推送 `v1.0.2` 形式的标签也会触发，版本从标签读取。预发行后缀不支持。
- 先运行 .NET 测试，成功后分别生成两个架构的 APPX 和侧载 ZIP。
- 在运行详情的 Artifacts 下载对应架构产物，保留 14 天。工作流不创建 GitHub Release，
  也不自动提交 Microsoft Store。无需配置签名 Secrets：使用每次临时生成的开发证书。
- 每个架构只上传 `SurgeLink_<版本>_<架构>.zip` 一个文件，使用 `archive: false`
  直接下载已有 ZIP。解压一次即可得到 APPX、公开证书和 `Install.ps1`，没有内层 ZIP，
  也不会再附带一份重复的 APPX。商店提交所需 APPX 从该 ZIP 提取。
- ARM64 启动和实际功能仍需在 ARM64 Windows 设备验证。
  上传仅允许安装包、公钥证书和哈希，
  不上传整个构建目录或私钥。

本地构建：

```powershell
pwsh -NoProfile -File tools/package-appx.ps1 -Version 1.0.2 -Architecture x64
pwsh -NoProfile -File tools/package-appx.ps1 -Version 1.0.2 -Architecture arm64
```

x64 保持输出到 `artifacts/appx`；ARM64 输出到 `artifacts/appx/arm64`。
两个清单分别写入对应的 ProcessorArchitecture，文件名也带架构。

参考：[setup-dotnet](https://github.com/actions/setup-dotnet)、
[upload-artifact](https://github.com/actions/upload-artifact)。

## Microsoft Store 提交

`PublisherDisplayName` 必须与 Partner Center 的发布者显示名称完全一致，本项目使用 `AviderMin`。修改清单后必须重新打包并上传新的 APPX；旧文件不会自动更新。

清单使用 Partner Center“产品标识”页面提供的商店身份：

- Package/Identity/Name：`AviderMin.SurgeLink`
- Package/Identity/Publisher：`CN=BBD3BA0A-0CEC-419C-A0D8-D429C7E33E43`
- Package/Properties/PublisherDisplayName：`AviderMin`
- Package Family Name：`AviderMin.SurgeLink_nzt6g5yp3w11y`
- Store ID：`9NWDWNN7839R`

打包脚本从清单读取 Publisher 作为临时开发签名证书的 Subject，保证二者一致。证书仍是自签名开发证书；商店身份不代表证书已获得公开信任。PFN、Package SID 和 MSA 应用 ID 不需要填入这些清单字段。

`runFullTrust` 对应当前的 `Windows.FullTrustApplication` 桌面入口，应保留。在 Partner Center 提交时填写受限功能用途说明并申请批准；本地打包成功不代表商店已批准。可根据实际功能填写以下英文说明：

> SurgeLink is a .NET / WinUI desktop application for SSH terminal sessions and SFTP file transfers. The runFullTrust capability is required to launch its Windows.FullTrustApplication desktop entry point and support user-initiated network connections and local file access for file transfers.

上传后需再次确认包接受验证结果。商店身份匹配和受限功能批准由 Partner Center 验证。

运行 `pwsh -NoProfile -File tools/package-appx.ps1`。默认版本为 1.0.2，APPX 清单版本为 1.0.2.0。

发布结果：`artifacts/appx/SurgeLink_1.0.2_x64.zip`，包含且仅包含：

- `SurgeLink_1.0.2.0_x64.appx`
- `SurgeLink-Development.cer`
- `Install.ps1`

先完整解压 ZIP，再在解压目录执行 `powershell -NoProfile -File .\Install.ps1`。脚本先校验 APPX SHA256、证书指纹和有效期；如证书尚未受信任，将通过标准 UAC 请求管理员权限，仅将配套证书导入本地计算机 TrustedPeople。APPX 安装由原用户进程执行。脚本不导入根证书、不删除用户数据。

可使用 `powershell -NoProfile -File .\Install.ps1 -ValidateOnly` 仅校验文件，不修改证书存储或安装应用。若系统因互联网下载标记阻止运行脚本，请在确认来源后，通过脚本文件属性的“解除锁定”再执行。

打包参数支持 `-Version 1.0.2` 或四段版本号；已完成发布时 `-SkipPublish` 可仅重打包。版本覆盖参数同时传递给 .NET 发布，避免应用内版本和包版本不一致。

默认 x64 Release，面向 Windows 11 22H2（10.0.22621）及以上，包含 .NET 和 Windows App SDK 运行库。应用 ICO、标题栏及关于页、安装包图标统一使用 `docs/LOGO.png`；ICO 由 `tools/make-icon.ps1` 生成。MakePRI 合并资源到 `resources.pri`，并检查 Reactor 启动资源；MakeAppx 执行包校验，SignTool 使用开发证书签名。

开发私钥仅用于此次签名，临时 PFX 完成后删除。每次重打包生成新证书，安装脚本会绑定本次证书指纹与 APPX 哈希；请分发同一个 ZIP 内的配套文件。此签名适合开发测试，公开发行应使用正式签名渠道。

1.0.2 验证：Release 发布成功；APPX 清单 1.0.2.0、程序集文件版本 1.0.2.0、产品版本 1.0.2；合并资源存在；密码学签名有效且签名证书与 CER 一致；Windows PowerShell 5.1 执行 Install.ps1 -ValidateOnly 成功；ZIP 包含指定三个文件。此次没有导入新证书或安装 1.0.2。

## 普通 EXE 安装程序

先安装 [Inno Setup 6.3 或更高版本](https://jrsoftware.org/isinfo.php)，然后在仓库根目录执行：

```powershell
pwsh -NoProfile -File tools/package-exe.ps1
pwsh -NoProfile -File tools/package-exe.ps1 -Architecture arm64
```

默认读取 `Directory.Build.props` 中的版本，也可传入 `-Version 1.0.2.5`。
安装包输出到 `artifacts/installer/<架构>/SurgeLink-Setup-<版本>-<架构>.exe`，附带 SHA256 文件。
如编译器安装在其他位置，可使用 `-IsccPath "D:\Tools\Inno Setup 6\ISCC.exe"`。
使用 `-PublishOnly` 可仅发布完整运行目录，不需要 Inno Setup。

脚本每次使用新的 staging 目录发布 Release，将 .NET、Windows App SDK、资源及许可说明一起打包。
安装器面向 Windows 11 22H2 及以上，默认按当前用户安装到 `%LOCALAPPDATA%\Programs\SurgeLink`，
创建开始菜单入口、可选桌面快捷方式以及卸载入口；卸载保留 `%LOCALAPPDATA%\SurgeLink` 中的用户数据。
EXE 安装器和 APPX 是独立的安装渠道，不会自动迁移 APPX 数据或卸载 APPX。
此脚本不执行安装，也不自动签名；ARM64 包仍需在对应设备验证。

EXE 发布检查包含应用自身的 `SurgeLink.pri`，缺失时直接停止打包；仅包含依赖库的 PRI 无法保证 WinUI 正常启动。

### GitHub Actions EXE 打包

工作流 `.github/workflows/package-exe.yml` 在 Actions 中显示为 **Package EXE Installer**。
支持手动 Run workflow（版本留空读取 `Directory.Build.props`）及推送 `v1.0.2.5` 形式标签触发。
先执行测试，再在 Windows runner 上分别生成 x64、ARM64 安装包；沿用 `tools/package-exe.ps1`，
优先使用 runner 自带 Inno Setup，缺失时通过 Chocolatey 安装。
x64 会实际静默安装并检查程序启动后持续运行十秒，ARM64 为交叉编译，仍需真机验证。
每个架构的 EXE 和 SHA256 分别作为原始文件上传，保存 14 天；不上传发布目录或创建 GitHub Release。
此工作流与现有 APPX 工作流独立，推送版本标签时两个工作流都会运行。安装包暂未签名。
