# APPX 打包

运行 `pwsh -NoProfile -File tools/package-appx.ps1`，输出位于 `artifacts/appx/`。
已完成自包含发布时，可使用 `-SkipPublish` 仅重新生成包；版本参数为 `-Version 0.1.0.0`。

默认构建为 x64 Release，面向 Windows 11 22H2（10.0.22621）及以上版本，包含 .NET 和 Windows App SDK 运行库。使用 Windows SDK MakeAppx 执行清单校验和打包，再由 SignTool 签名。

## 本次输出

- `SurgeLink_0.1.0.0_x64.appx`：开发签名安装包。
- `SurgeLink-Development.cer`：对应的公钥证书。
- `SHA256.txt`：签名完成后的包文件校验值。

## 安装

本次没有自动安装应用或添加证书信任。开发证书尚未被 Windows 信任，直接双击 APPX 可能报告不受信任的发布者。

在确认信任本次构建后，手动打开 `.cer`，选择“安装证书”，将其安装到“本地计算机”的“受信任人”证书存储（需要管理员权限），然后双击 `.appx` 安装。不要将它放入“受信任的根证书颁发机构”。

证书仅用于开发测试，并非公开发行用的受信任代码签名证书。脚本在内存生成开发私钥，签名完成后删除临时 PFX；每次重新打包会生成新证书，重新分发时应附上对应 CER。需要稳定的企业或公开发行渠道时，应替换为正式签名流程。

## 本次验证

- Release 自包含发布成功；还原阶段因网络无法访问 NuGet 漏洞数据服务而产生 NU1900 警告。
- MakeAppx 未跳过校验，包创建成功。
- SignTool 签名成功；CMS 签名密码学校验通过，签名证书与输出 CER 匹配。
- 核对了清单、块映射、签名、应用 EXE、.NET 运行库、WinUI 运行库和包图标。
- SignTool 的 Windows 信任链验证返回“不受信任根证书”，这是开发证书未导入信任存储的预期结果；不等于已经通过系统信任验证。
- 未进行 APPX 安装和安装后的启动验证。

参考：[Windows App SDK 自包含部署](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)。
