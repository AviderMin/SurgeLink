# APPX 与 ZIP 发布

运行 `pwsh -NoProfile -File tools/package-appx.ps1`。默认版本为 1.0.2，APPX 清单版本为 1.0.2.0。

发布结果：`artifacts/appx/SurgeLink_1.0.2_x64.zip`，包含且仅包含：

- `SurgeLink_1.0.2.0_x64.appx`
- `SurgeLink-Development.cer`
- `Install.ps1`

先完整解压 ZIP，再在解压目录执行 `powershell -NoProfile -File .\Install.ps1`。脚本先校验 APPX SHA256、证书指纹和有效期；如证书尚未受信任，将通过标准 UAC 请求管理员权限，仅将配套证书导入本地计算机 TrustedPeople。APPX 安装由原用户进程执行。脚本不导入根证书、不删除用户数据。

可使用 `powershell -NoProfile -File .\Install.ps1 -ValidateOnly` 仅校验文件，不修改证书存储或安装应用。若系统因互联网下载标记阻止运行脚本，请在确认来源后，通过脚本文件属性的“解除锁定”再执行。

打包参数支持 `-Version 1.0.2` 或四段版本号；已完成发布时 `-SkipPublish` 可仅重打包。版本覆盖参数同时传递给 .NET 发布，避免应用内版本和包版本不一致。

默认 x64 Release，面向 Windows 11 22H2（10.0.22621）及以上，包含 .NET 和 Windows App SDK 运行库。图标从 `docs/ICON.jpg` 生成。MakePRI 合并资源到 `resources.pri`，并检查 Reactor 启动资源；MakeAppx 执行包校验，SignTool 使用开发证书签名。

开发私钥仅用于此次签名，临时 PFX 完成后删除。每次重打包生成新证书，安装脚本会绑定本次证书指纹与 APPX 哈希；请分发同一个 ZIP 内的配套文件。此签名适合开发测试，公开发行应使用正式签名渠道。

1.0.2 验证：Release 发布成功；APPX 清单 1.0.2.0、程序集文件版本 1.0.2.0、产品版本 1.0.2；合并资源存在；密码学签名有效且签名证书与 CER 一致；Windows PowerShell 5.1 执行 Install.ps1 -ValidateOnly 成功；ZIP 包含指定三个文件。此次没有导入新证书或安装 1.0.2。
