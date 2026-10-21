# SurgeLink 黑盒测试用例

基线：1.0.2 / APPX 1.0.2.0。共 147 项：43 项脚本断言、104 项手工/环境用例。这里的状态均为设计态 NotRun；实际结果见每次运行生成的 results.json，不能把用例数量当作通过数量。

自动用例的精确步骤与断言由对应脚本定义。手工用例必须记录操作系统、应用版本、环境夹具、实际结果和证据。

| ID | 模块 | 优先级 | 执行方式 | 标题/目的 | 前置条件 | 步骤/输入 | 预期结果 |
|---|---|---|---|---|---|---|---|
| PKG001 | 安装包自动化 | P0 | Test-Package.ps1 | ZIP opens and contains exactly APPX, CER and installer | 本次发布ZIP；Windows PowerShell可用；无需管理员 | 执行 tools/blackbox/Test-Package.ps1；按报告中的 PKG001 检查独立断言。 | ZIP opens and contains exactly APPX, CER and installer |
| PKG002 | 安装包自动化 | P0 | Test-Package.ps1 | Distribution contains no private signing key | 本次发布ZIP；Windows PowerShell可用；无需管理员 | 执行 tools/blackbox/Test-Package.ps1；按报告中的 PKG002 检查独立断言。 | Distribution contains no private signing key |
| PKG003 | 安装包自动化 | P0 | Test-Package.ps1 | APPX opens with required package metadata | 本次发布ZIP；Windows PowerShell可用；无需管理员 | 执行 tools/blackbox/Test-Package.ps1；按报告中的 PKG003 检查独立断言。 | APPX opens with required package metadata |
| PKG004 | 安装包自动化 | P0 | Test-Package.ps1 | Package version matches requested release | 本次发布ZIP；Windows PowerShell可用；无需管理员 | 执行 tools/blackbox/Test-Package.ps1；按报告中的 PKG004 检查独立断言。 | Package version matches requested release |
| PKG005 | 安装包自动化 | P0 | Test-Package.ps1 | Package targets x64 Windows Desktop | 本次发布ZIP；Windows PowerShell可用；无需管理员 | 执行 tools/blackbox/Test-Package.ps1；按报告中的 PKG005 检查独立断言。 | Package targets x64 Windows Desktop |
| PKG006 | 安装包自动化 | P0 | Test-Package.ps1 | Launch entry resolves to shipped executable | 本次发布ZIP；Windows PowerShell可用；无需管理员 | 执行 tools/blackbox/Test-Package.ps1；按报告中的 PKG006 检查独立断言。 | Launch entry resolves to shipped executable |
| PKG007 | 安装包自动化 | P0 | Test-Package.ps1 | Merged startup resource index exists and is nonempty | 本次发布ZIP；Windows PowerShell可用；无需管理员 | 执行 tools/blackbox/Test-Package.ps1；按报告中的 PKG007 检查独立断言。 | Merged startup resource index exists and is nonempty |
| PKG008 | 安装包自动化 | P0 | Test-Package.ps1 | Self-contained .NET and WinUI runtime present | 本次发布ZIP；Windows PowerShell可用；无需管理员 | 执行 tools/blackbox/Test-Package.ps1；按报告中的 PKG008 检查独立断言。 | Self-contained .NET and WinUI runtime present |
| PKG009 | 安装包自动化 | P0 | Test-Package.ps1 | Manifest icons exist in package | 本次发布ZIP；Windows PowerShell可用；无需管理员 | 执行 tools/blackbox/Test-Package.ps1；按报告中的 PKG009 检查独立断言。 | Manifest icons exist in package |
| PKG010 | 安装包自动化 | P0 | Test-Package.ps1 | Development certificate is readable and currently valid | 本次发布ZIP；Windows PowerShell可用；无需管理员 | 执行 tools/blackbox/Test-Package.ps1；按报告中的 PKG010 检查独立断言。 | Development certificate is readable and currently valid |
| PKG011 | 安装包自动化 | P0 | Test-Package.ps1 | Certificate grants code signing usage | 本次发布ZIP；Windows PowerShell可用；无需管理员 | 执行 tools/blackbox/Test-Package.ps1；按报告中的 PKG011 检查独立断言。 | Certificate grants code signing usage |
| PKG012 | 安装包自动化 | P0 | Test-Package.ps1 | APPX CMS cryptographic signature is valid | 本次发布ZIP；Windows PowerShell可用；无需管理员 | 执行 tools/blackbox/Test-Package.ps1；按报告中的 PKG012 检查独立断言。 | APPX CMS cryptographic signature is valid |
| PKG013 | 安装包自动化 | P0 | Test-Package.ps1 | Signing certificate matches CER and manifest publisher | 本次发布ZIP；Windows PowerShell可用；无需管理员 | 执行 tools/blackbox/Test-Package.ps1；按报告中的 PKG013 检查独立断言。 | Signing certificate matches CER and manifest publisher |
| PKG014 | 安装包自动化 | P0 | Test-Package.ps1 | Installer PowerShell syntax is valid | 本次发布ZIP；Windows PowerShell可用；无需管理员 | 执行 tools/blackbox/Test-Package.ps1；按报告中的 PKG014 检查独立断言。 | Installer PowerShell syntax is valid |
| PKG015 | 安装包自动化 | P0 | Test-Package.ps1 | Installer validates authentic files without installation | 本次发布ZIP；Windows PowerShell可用；无需管理员 | 执行 tools/blackbox/Test-Package.ps1；按报告中的 PKG015 检查独立断言。 | Installer validates authentic files without installation |
| PKG016 | 安装包自动化 | P0 | Test-Package.ps1 | Installer rejects tampered APPX | 本次发布ZIP；Windows PowerShell可用；无需管理员 | 执行 tools/blackbox/Test-Package.ps1；按报告中的 PKG016 检查独立断言。 | Installer rejects tampered APPX |
| PKG017 | 安装包自动化 | P0 | Test-Package.ps1 | Installer rejects missing certificate | 本次发布ZIP；Windows PowerShell可用；无需管理员 | 执行 tools/blackbox/Test-Package.ps1；按报告中的 PKG017 检查独立断言。 | Installer rejects missing certificate |
| PKG018 | 安装包自动化 | P0 | Test-Package.ps1 | Installer rejects missing APPX | 本次发布ZIP；Windows PowerShell可用；无需管理员 | 执行 tools/blackbox/Test-Package.ps1；按报告中的 PKG018 检查独立断言。 | Installer rejects missing APPX |
| UI001 | 桌面UI自动化 | P1 | Test-Desktop.ps1 | Application starts and exposes its own main window | 独立Windows测试账号/VM；可交互桌面；关闭其他SurgeLink；指定已构建EXE | 执行 tools/blackbox/Test-Desktop.ps1；按报告中的 UI001 检查独立断言。 | Application starts and exposes its own main window |
| UI002 | 桌面UI自动化 | P1 | Test-Desktop.ps1 | Main title is SurgeLink | 独立Windows测试账号/VM；可交互桌面；关闭其他SurgeLink；指定已构建EXE | 执行 tools/blackbox/Test-Desktop.ps1；按报告中的 UI002 检查独立断言。 | Main title is SurgeLink |
| UI003 | 桌面UI自动化 | P1 | Test-Desktop.ps1 | Shell contains no render error | 独立Windows测试账号/VM；可交互桌面；关闭其他SurgeLink；指定已构建EXE | 执行 tools/blackbox/Test-Desktop.ps1；按报告中的 UI003 检查独立断言。 | Shell contains no render error |
| UI004 | 桌面UI自动化 | P1 | Test-Desktop.ps1 | Settings opens as native dialog | 独立Windows测试账号/VM；可交互桌面；关闭其他SurgeLink；指定已构建EXE | 执行 tools/blackbox/Test-Desktop.ps1；按报告中的 UI004 检查独立断言。 | Settings opens as native dialog |
| UI005 | 桌面UI自动化 | P1 | Test-Desktop.ps1 | Settings exposes language input | 独立Windows测试账号/VM；可交互桌面；关闭其他SurgeLink；指定已构建EXE | 执行 tools/blackbox/Test-Desktop.ps1；按报告中的 UI005 检查独立断言。 | Settings exposes language input |
| UI006 | 桌面UI自动化 | P1 | Test-Desktop.ps1 | Settings exposes theme input | 独立Windows测试账号/VM；可交互桌面；关闭其他SurgeLink；指定已构建EXE | 执行 tools/blackbox/Test-Desktop.ps1；按报告中的 UI006 检查独立断言。 | Settings exposes theme input |
| UI007 | 桌面UI自动化 | P1 | Test-Desktop.ps1 | Settings exposes terminal font input | 独立Windows测试账号/VM；可交互桌面；关闭其他SurgeLink；指定已构建EXE | 执行 tools/blackbox/Test-Desktop.ps1；按报告中的 UI007 检查独立断言。 | Settings exposes terminal font input |
| UI008 | 桌面UI自动化 | P1 | Test-Desktop.ps1 | Settings Save and Cancel are accessible and enabled | 独立Windows测试账号/VM；可交互桌面；关闭其他SurgeLink；指定已构建EXE | 执行 tools/blackbox/Test-Desktop.ps1；按报告中的 UI008 检查独立断言。 | Settings Save and Cancel are accessible and enabled |
| UI009 | 桌面UI自动化 | P1 | Test-Desktop.ps1 | Settings Cancel dismisses dialog | 独立Windows测试账号/VM；可交互桌面；关闭其他SurgeLink；指定已构建EXE | 执行 tools/blackbox/Test-Desktop.ps1；按报告中的 UI009 检查独立断言。 | Settings Cancel dismisses dialog |
| UI010 | 桌面UI自动化 | P1 | Test-Desktop.ps1 | Settings can reopen and dismiss without error | 独立Windows测试账号/VM；可交互桌面；关闭其他SurgeLink；指定已构建EXE | 执行 tools/blackbox/Test-Desktop.ps1；按报告中的 UI010 检查独立断言。 | Settings can reopen and dismiss without error |
| UI011 | 桌面UI自动化 | P1 | Test-Desktop.ps1 | About opens as native dialog | 独立Windows测试账号/VM；可交互桌面；关闭其他SurgeLink；指定已构建EXE | 执行 tools/blackbox/Test-Desktop.ps1；按报告中的 UI011 检查独立断言。 | About opens as native dialog |
| UI012 | 桌面UI自动化 | P1 | Test-Desktop.ps1 | About shows installed executable version | 独立Windows测试账号/VM；可交互桌面；关闭其他SurgeLink；指定已构建EXE | 执行 tools/blackbox/Test-Desktop.ps1；按报告中的 UI012 检查独立断言。 | About shows installed executable version |
| UI013 | 桌面UI自动化 | P1 | Test-Desktop.ps1 | About Close dismisses dialog | 独立Windows测试账号/VM；可交互桌面；关闭其他SurgeLink；指定已构建EXE | 执行 tools/blackbox/Test-Desktop.ps1；按报告中的 UI013 检查独立断言。 | About Close dismisses dialog |
| UI014 | 桌面UI自动化 | P1 | Test-Desktop.ps1 | New session editor opens | 独立Windows测试账号/VM；可交互桌面；关闭其他SurgeLink；指定已构建EXE | 执行 tools/blackbox/Test-Desktop.ps1；按报告中的 UI014 检查独立断言。 | New session editor opens |
| UI015 | 桌面UI自动化 | P1 | Test-Desktop.ps1 | Empty host is rejected and editor stays open | 独立Windows测试账号/VM；可交互桌面；关闭其他SurgeLink；指定已构建EXE | 执行 tools/blackbox/Test-Desktop.ps1；按报告中的 UI015 检查独立断言。 | Empty host is rejected and editor stays open |
| UI016 | 桌面UI自动化 | P1 | Test-Desktop.ps1 | Invalid port '0' is rejected | 独立Windows测试账号/VM；可交互桌面；关闭其他SurgeLink；指定已构建EXE | 执行 tools/blackbox/Test-Desktop.ps1；按报告中的 UI016 检查独立断言。 | Invalid port '0' is rejected |
| UI017 | 桌面UI自动化 | P1 | Test-Desktop.ps1 | Invalid port '-1' is rejected | 独立Windows测试账号/VM；可交互桌面；关闭其他SurgeLink；指定已构建EXE | 执行 tools/blackbox/Test-Desktop.ps1；按报告中的 UI017 检查独立断言。 | Invalid port '-1' is rejected |
| UI018 | 桌面UI自动化 | P1 | Test-Desktop.ps1 | Invalid port '65536' is rejected | 独立Windows测试账号/VM；可交互桌面；关闭其他SurgeLink；指定已构建EXE | 执行 tools/blackbox/Test-Desktop.ps1；按报告中的 UI018 检查独立断言。 | Invalid port '65536' is rejected |
| UI019 | 桌面UI自动化 | P1 | Test-Desktop.ps1 | Invalid port 'abc' is rejected | 独立Windows测试账号/VM；可交互桌面；关闭其他SurgeLink；指定已构建EXE | 执行 tools/blackbox/Test-Desktop.ps1；按报告中的 UI019 检查独立断言。 | Invalid port 'abc' is rejected |
| UI020 | 桌面UI自动化 | P1 | Test-Desktop.ps1 | Invalid port '22.5' is rejected | 独立Windows测试账号/VM；可交互桌面；关闭其他SurgeLink；指定已构建EXE | 执行 tools/blackbox/Test-Desktop.ps1；按报告中的 UI020 检查独立断言。 | Invalid port '22.5' is rejected |
| UI021 | 桌面UI自动化 | P1 | Test-Desktop.ps1 | Session editor Cancel closes invalid draft | 独立Windows测试账号/VM；可交互桌面；关闭其他SurgeLink；指定已构建EXE | 执行 tools/blackbox/Test-Desktop.ps1；按报告中的 UI021 检查独立断言。 | Session editor Cancel closes invalid draft |
| UI022 | 桌面UI自动化 | P1 | Test-Desktop.ps1 | Session filter accepts test query | 独立Windows测试账号/VM；可交互桌面；关闭其他SurgeLink；指定已构建EXE | 执行 tools/blackbox/Test-Desktop.ps1；按报告中的 UI022 检查独立断言。 | Session filter accepts test query |
| UI023 | 桌面UI自动化 | P1 | Test-Desktop.ps1 | Session filter can be cleared | 独立Windows测试账号/VM；可交互桌面；关闭其他SurgeLink；指定已构建EXE | 执行 tools/blackbox/Test-Desktop.ps1；按报告中的 UI023 检查独立断言。 | Session filter can be cleared |
| UI024 | 桌面UI自动化 | P1 | Test-Desktop.ps1 | Application survives dialog and validation sequence | 独立Windows测试账号/VM；可交互桌面；关闭其他SurgeLink；指定已构建EXE | 执行 tools/blackbox/Test-Desktop.ps1；按报告中的 UI024 检查独立断言。 | Application survives dialog and validation sequence |
| UI025 | 桌面UI自动化 | P1 | Test-Desktop.ps1 | Main window closes cleanly | 独立Windows测试账号/VM；可交互桌面；关闭其他SurgeLink；指定已构建EXE | 执行 tools/blackbox/Test-Desktop.ps1；按报告中的 UI025 检查独立断言。 | Main window closes cleanly |
| M01-001 | 安装与升级 | P0 | Manual | 全新安装 | 干净的 Windows 11 x64 测试虚拟机；仅使用本次 ZIP 与配套证书 | 解压 ZIP；运行 Install.ps1；允许标准 UAC；从开始菜单打开应用 | 安装成功，开始菜单显示新图标，主窗口正常显示，无证书或资源错误 |
| M01-002 | 安装与升级 | P0 | Manual | 已安装旧版升级 | 干净的 Windows 11 x64 测试虚拟机；仅使用本次 ZIP 与配套证书 | 旧版保存测试会话和偏好；安装新版；重启应用 | 包版本升级，测试会话及偏好仍可使用，应用正常启动 |
| M01-003 | 安装与升级 | P1 | Manual | UAC 拒绝 | 干净的 Windows 11 x64 测试虚拟机；仅使用本次 ZIP 与配套证书 | 移除本次测试证书的快照环境中运行脚本；拒绝 UAC | 脚本明确失败，无 APPX 安装成功提示，不改变应用版本 |
| M01-004 | 安装与升级 | P0 | Manual | 错误发布证书 | 干净的 Windows 11 x64 测试虚拟机；仅使用本次 ZIP 与配套证书 | 将 CER 换为另一发布者的有效 CER；运行 -ValidateOnly | 拒绝不匹配证书，不导入信任、不安装 |
| M01-005 | 安装与升级 | P1 | Manual | 安装路径含空格和中文 | 干净的 Windows 11 x64 测试虚拟机；仅使用本次 ZIP 与配套证书 | 将 ZIP 解压到 C:\测试 发布\SurgeLink；运行安装脚本 | 路径引用正确，校验、提权和安装均可完成 |
| M01-006 | 安装与升级 | P1 | Manual | 无管理员凭据 | 干净的 Windows 11 x64 测试虚拟机；仅使用本次 ZIP 与配套证书 | 标准用户运行安装；UAC 中取消凭据输入 | 失败可理解，不出现安装成功，不删除现有版本和用户数据 |
| M01-007 | 安装与升级 | P1 | Manual | 重复安装同版本 | 干净的 Windows 11 x64 测试虚拟机；仅使用本次 ZIP 与配套证书 | 同一 ZIP 连续安装两次；启动应用 | 同版本可重装或返回明确的已安装信息，现有数据不丢失 |
| M01-008 | 安装与升级 | P1 | Manual | 最低系统与架构 | 干净的 Windows 11 x64 测试虚拟机；仅使用本次 ZIP 与配套证书 | 分别在低于 10.0.22621 系统、x86 系统尝试安装 x64 包 | 安装器明确拒绝不支持的系统或架构，无半安装状态 |
| M02-001 | 会话管理 | P1 | Manual | 端口下界合法 | 独立测试账号；会话名称统一 BB_<随机后缀>；仅操作测试会话 | 新建 SSH 会话，主机填测试地址，端口 1；仅保存 | 表单接受合法端口，不自动发起连接 |
| M02-002 | 会话管理 | P1 | Manual | 端口上界合法 | 独立测试账号；会话名称统一 BB_<随机后缀>；仅操作测试会话 | 新建 SSH 会话，端口 65535；仅保存 | 表单接受 65535 并保存，重新编辑仍为 65535 |
| M02-003 | 会话管理 | P1 | Manual | 空端口默认值 | 独立测试账号；会话名称统一 BB_<随机后缀>；仅操作测试会话 | 新建 SSH 会话，端口留空；保存后编辑 | 使用 SSH 默认端口 22；不保存为非法端口 |
| M02-004 | 会话管理 | P1 | Manual | 用户名空白 | 独立测试账号；会话名称统一 BB_<随机后缀>；仅操作测试会话 | 新建会话；用户名分别为空串、三个空格；点击保存 | 均显示用户名必填提示，弹窗保持打开，不保存 |
| M02-005 | 会话管理 | P1 | Manual | 中文与长名称 | 独立测试账号；会话名称统一 BB_<随机后缀>；仅操作测试会话 | 名称分别为 中文连接、256 字符字符串；保存并搜索 | 不崩溃；列表正确显示或省略且可识别，不截断关键配置 |
| M02-006 | 会话管理 | P1 | Manual | 分组与搜索 | 独立测试账号；会话名称统一 BB_<随机后缀>；仅操作测试会话 | 创建两个不同分组会话；按名称和主机关键字搜索；清除筛选 | 筛选结果匹配；清除后恢复全部；分组标题不重复 |
| M02-007 | 会话管理 | P0 | Manual | 编辑会话 | 独立测试账号；会话名称统一 BB_<随机后缀>；仅操作测试会话 | 编辑测试会话的名称、端口、分组；保存并重启 | 修改持久化，未创建额外副本 |
| M02-008 | 会话管理 | P1 | Manual | 复制会话 | 独立测试账号；会话名称统一 BB_<随机后缀>；仅操作测试会话 | 对测试会话选择复制；修改名称并保存；重新编辑原会话 | 副本独立，原会话参数不变 |
| M02-009 | 会话管理 | P1 | Manual | 删除取消 | 独立测试账号；会话名称统一 BB_<随机后缀>；仅操作测试会话 | 删除测试会话；在确认框选择取消 | 原会话仍在，可编辑、连接 |
| M02-010 | 会话管理 | P1 | Manual | 删除确认 | 独立测试账号；会话名称统一 BB_<随机后缀>；仅操作测试会话 | 删除明确标识的测试会话；确认；重启 | 该会话不再显示；其他会话保留；已打开标签的行为符合提示 |
| M03-001 | SSH连接与安全 | P0 | Manual | 密码连接成功 | 隔离 Linux/Windows OpenSSH 测试机；凭据从测试夹具提供；禁止生产服务器 | 使用有效测试账户、密码连接 Linux SSH | 成功进入终端，状态为已连接，主机信息与夹具一致 |
| M03-002 | SSH连接与安全 | P0 | Manual | 密码错误 | 隔离 Linux/Windows OpenSSH 测试机；凭据从测试夹具提供；禁止生产服务器 | 使用正确用户名和错误测试密码连接 | 明确认证失败；不会显示已连接；应用可继续操作 |
| M03-003 | SSH连接与安全 | P0 | Manual | 私钥与口令 | 隔离 Linux/Windows OpenSSH 测试机；凭据从测试夹具提供；禁止生产服务器 | 选择受密码保护的测试私钥；输入正确口令连接 | 连接成功，密码框内容不以明文显示 |
| M03-004 | SSH连接与安全 | P1 | Manual | 私钥文件缺失 | 隔离 Linux/Windows OpenSSH 测试机；凭据从测试夹具提供；禁止生产服务器 | 选择已删除的测试私钥路径后连接 | 提示文件/认证错误，无崩溃或无限等待 |
| M03-005 | SSH连接与安全 | P0 | Manual | 首次主机密钥拒绝 | 隔离 Linux/Windows OpenSSH 测试机；凭据从测试夹具提供；禁止生产服务器 | 清洁测试信任记录；连接测试 SSH；选择拒绝 | 连接终止，不进入已连接，不永久信任该密钥 |
| M03-006 | SSH连接与安全 | P0 | Manual | 仅本次信任 | 隔离 Linux/Windows OpenSSH 测试机；凭据从测试夹具提供；禁止生产服务器 | 首次连接选择仅本次信任；关闭连接并重启再连接 | 首次可连接；重启后再次请求核验密钥 |
| M03-007 | SSH连接与安全 | P0 | Manual | 永久信任与密钥变化 | 隔离 Linux/Windows OpenSSH 测试机；凭据从测试夹具提供；禁止生产服务器 | 在测试机永久信任密钥A；更换测试服务端密钥B后重连 | 显示密钥已变更警告；未确认前不得静默连接 |
| M03-008 | SSH连接与安全 | P1 | Manual | 交互式认证 | 隔离 Linux/Windows OpenSSH 测试机；凭据从测试夹具提供；禁止生产服务器 | 使用测试 keyboard-interactive 账户；完成夹具挑战 | 交互框可输入并完成认证；取消时连接终止 |
| M03-009 | SSH连接与安全 | P1 | Manual | 连接超时与拒绝 | 隔离 Linux/Windows OpenSSH 测试机；凭据从测试夹具提供；禁止生产服务器 | 分别连接测试防火墙丢弃端口、已关闭端口 | 均在有限等待后显示失败；状态恢复且可重试 |
| M03-010 | SSH连接与安全 | P0 | Manual | 断网恢复 | 隔离 Linux/Windows OpenSSH 测试机；凭据从测试夹具提供；禁止生产服务器 | 已连接测试会话时断开虚拟网络；恢复网络并重连 | 状态反映断线；失败可解释；恢复后可重新连接，无旧采样串入 |
| M04-001 | 终端 | P0 | Manual | UTF-8 与中文 | Demo 或隔离 SSH；只执行测试命令，不操作用户文件 | 在测试终端输出 ASCII、中文、emoji、组合字符 | 编码不乱码；宽字符不覆盖相邻字符；光标位置合理 |
| M04-002 | 终端 | P1 | Manual | ANSI 颜色样式 | Demo 或隔离 SSH；只执行测试命令，不操作用户文件 | 输出固定 16/256/真彩色及粗体、下划线、反色测试串 | 样式与夹具一致，重置序列后后续文字恢复默认 |
| M04-003 | 终端 | P1 | Manual | 连续输出回滚 | Demo 或隔离 SSH；只执行测试命令，不操作用户文件 | 输出编号 1..20000 行；滚动至历史再回底部 | 回滚可操作，行序正确，界面不长时间卡死 |
| M04-004 | 终端 | P0 | Manual | 窗口缩放与PTY | Demo 或隔离 SSH；只执行测试命令，不操作用户文件 | 运行显示终端列行数的测试程序；多次缩放窗口 | 列行数随可用区域变化，输入和光标仍对齐 |
| M04-005 | 终端 | P1 | Manual | 选择与复制 | Demo 或隔离 SSH；只执行测试命令，不操作用户文件 | 选择多行含中文输出并复制到空白测试文本文件 | 复制文本与选中内容一致，无丢字或 ANSI 逃逸码污染 |
| M04-006 | 终端 | P1 | Manual | 粘贴多行 | Demo 或隔离 SSH；只执行测试命令，不操作用户文件 | 粘贴事先审核的三行无副作用测试命令 | 换行/回车处理与服务器 shell 预期一致，不重复输入 |
| M04-007 | 终端 | P1 | Manual | 常用控制键 | Demo 或隔离 SSH；只执行测试命令，不操作用户文件 | 测试 Backspace、方向键、Home/End、Ctrl+C | 发送键序列正确，编辑与中断行为符合远端程序 |
| M04-008 | 终端 | P1 | Manual | 备用屏幕 | Demo 或隔离 SSH；只执行测试命令，不操作用户文件 | 在测试 SSH 打开、退出 vim/less 等全屏程序 | 退出后主屏历史恢复；光标与文本不串屏 |
| M04-009 | 终端 | P1 | Manual | 快速切换标签 | Demo 或隔离 SSH；只执行测试命令，不操作用户文件 | 两个测试终端分别输出唯一标识；反复切换 | 两个终端内容保持独立，不出现字符串入另一会话 |
| M04-010 | 终端 | P1 | Manual | 字体与配色更新 | Demo 或隔离 SSH；只执行测试命令，不操作用户文件 | 在设置更改等宽字体、字号和配色并保存；返回终端 | 现有终端更新显示；内容仍在，PTY 尺寸与文字度量一致 |
| M05-001 | 文件浏览与操作 | P0 | Manual | 初始目录 | Demo 内存文件系统或测试 SSH 的独立 BB_<随机后缀> 目录 | 打开 Demo；切换文件视图 | 显示 /home/demo 的 demo.txt、Documents、Projects 等夹具内容 |
| M05-002 | 文件浏览与操作 | P0 | Manual | 目录进入与返回 | Demo 内存文件系统或测试 SSH 的独立 BB_<随机后缀> 目录 | 进入 Documents；使用面包屑返回 home/demo | 当前路径与列表同步，返回后不保留不可见项的选中状态 |
| M05-003 | 文件浏览与操作 | P1 | Manual | 根目录向上 | Demo 内存文件系统或测试 SSH 的独立 BB_<随机后缀> 目录 | 连续向上到 /；检查向上按钮并继续尝试 | 不越过根目录，不生成非法路径 |
| M05-004 | 文件浏览与操作 | P1 | Manual | 刷新保持目录 | Demo 内存文件系统或测试 SSH 的独立 BB_<随机后缀> 目录 | 进入子目录；在夹具侧新增测试文件；点击刷新 | 当前目录不变，新增项可见，无重复列表项 |
| M05-005 | 文件浏览与操作 | P1 | Manual | 名称与大小排序 | Demo 内存文件系统或测试 SSH 的独立 BB_<随机后缀> 目录 | 准备大小不同、名称不同的文件；分别升降序排序 | 按所选字段正确排序，目录和文件仍可操作 |
| M05-006 | 文件浏览与操作 | P1 | Manual | 隐藏文件偏好 | Demo 内存文件系统或测试 SSH 的独立 BB_<随机后缀> 目录 | 在设置切换显示隐藏文件并保存；返回文件视图 | .bashrc 等点文件显隐与保存设置一致 |
| M05-007 | 文件浏览与操作 | P1 | Manual | 无选择按钮状态 | Demo 内存文件系统或测试 SSH 的独立 BB_<随机后缀> 目录 | 文件列表清除选择；检查下载、重命名、删除、编辑 | 要求选择的操作禁用，工具提示和键盘操作不会绕过限制 |
| M05-008 | 文件浏览与操作 | P0 | Manual | 新建空文件 | Demo 内存文件系统或测试 SSH 的独立 BB_<随机后缀> 目录 | 新建唯一名称文件；刷新并读取 | 文件存在且长度为 0，列表自动更新 |
| M05-009 | 文件浏览与操作 | P0 | Manual | 新建目录 | Demo 内存文件系统或测试 SSH 的独立 BB_<随机后缀> 目录 | 新建唯一名称目录；进入该目录 | 目录存在，空状态可理解，路径正确 |
| M05-010 | 文件浏览与操作 | P1 | Manual | 重复名称 | Demo 内存文件系统或测试 SSH 的独立 BB_<随机后缀> 目录 | 创建与现有测试文件同名的文件或目录 | 给出明确冲突或要求确认，不静默丢失原数据 |
| M05-011 | 文件浏览与操作 | P1 | Manual | 非法名称 | Demo 内存文件系统或测试 SSH 的独立 BB_<随机后缀> 目录 | 分别输入空名、仅空格、含 / 的文件名 | 拒绝无效输入或显示明确远端错误，不创建错误路径 |
| M05-012 | 文件浏览与操作 | P0 | Manual | 重命名 | Demo 内存文件系统或测试 SSH 的独立 BB_<随机后缀> 目录 | 将测试文件 a.txt 重命名为 中文 b.txt；刷新 | 新名称可见，旧名称消失，文件内容与 SHA256 不变 |
| M05-013 | 文件浏览与操作 | P0 | Manual | 删除确认与取消 | Demo 内存文件系统或测试 SSH 的独立 BB_<随机后缀> 目录 | 分别对测试文件执行删除后取消、删除后确认 | 取消保留文件；确认删除目标且不影响同目录其他文件 |
| M05-014 | 文件浏览与操作 | P1 | Manual | 权限修改 | Demo 内存文件系统或测试 SSH 的独立 BB_<随机后缀> 目录 | 将测试文件 POSIX 权限从 0644 改 0600 并刷新 | 权限显示与服务端 stat 结果一致；不支持权限的平台禁用该操作 |
| M06-001 | 文件传输 | P0 | Manual | 上传下载零字节 | 隔离 SSH；本地与远端均使用专用目录；预先记录 SHA256 | 上传再下载 0 字节文件 | 两次完成，文件存在且长度为 0，无除零错误 |
| M06-002 | 文件传输 | P1 | Manual | 缓冲区边界 | 隔离 SSH；本地与远端均使用专用目录；预先记录 SHA256 | 分别上传下载 1、4095、4096、4097 字节二进制夹具 | 所有文件长度和 SHA256 与原文件一致 |
| M06-003 | 文件传输 | P0 | Manual | 大文件进度 | 隔离 SSH；本地与远端均使用专用目录；预先记录 SHA256 | 上传下载 100 MiB 测试文件；观察进度与窗口响应 | 进度可见且完成后为完成状态；SHA256 一致，界面可操作 |
| M06-004 | 文件传输 | P1 | Manual | 中文与空格路径 | 隔离 SSH；本地与远端均使用专用目录；预先记录 SHA256 | 上传下载 中文 空格.txt 到带空格子目录 | 路径和内容完整，不产生错误拆分命令 |
| M06-005 | 文件传输 | P0 | Manual | 多文件排队 | 隔离 SSH；本地与远端均使用专用目录；预先记录 SHA256 | 一次选择三个不同大小文件上传 | 均进入队列且最终完成，目标文件逐一校验一致 |
| M06-006 | 文件传输 | P1 | Manual | 取消排队任务 | 隔离 SSH；本地与远端均使用专用目录；预先记录 SHA256 | 队列中第二个任务未运行时取消 | 第二项取消，其他任务继续，未开始目标不出现完整成功文件 |
| M06-007 | 文件传输 | P0 | Manual | 取消运行任务 | 隔离 SSH；本地与远端均使用专用目录；预先记录 SHA256 | 运行大文件传输时取消 | 任务最终为已取消；不假报完成；队列后续任务可继续 |
| M06-008 | 文件传输 | P0 | Manual | 传输中断网 | 隔离 SSH；本地与远端均使用专用目录；预先记录 SHA256 | 大文件传输中断开测试网络 | 任务失败或取消且给出错误，不能显示成功；界面可继续操作 |
| M06-009 | 文件传输 | P1 | Manual | 无写权限与磁盘满 | 隔离 SSH；本地与远端均使用专用目录；预先记录 SHA256 | 分别上传到只读测试目录、受控满盘测试卷 | 明确失败原因，源文件保留，其他传输不被错误标成完成 |
| M06-010 | 文件传输 | P0 | Manual | SCP回退 | 隔离 SSH；本地与远端均使用专用目录；预先记录 SHA256 | 在禁用 SFTP 子系统的测试服务器上传下载 | 可走 SCP 回退，文件 SHA256 一致，路径正确转义 |
| M07-001 | 远程编辑器 | P0 | Manual | 打开文件 | 测试目录中 UTF-8 文件；变更可回滚；所有编辑均针对测试文件 | 选择 UTF-8 中文测试文件并点击编辑 | 独立编辑窗口显示完整内容及文件名，行号与内容一致 |
| M07-002 | 远程编辑器 | P0 | Manual | 保存并重开 | 测试目录中 UTF-8 文件；变更可回滚；所有编辑均针对测试文件 | 修改指定测试行；保存；关闭后再次打开 | 内容持久化且远端字节符合 UTF-8 预期 |
| M07-003 | 远程编辑器 | P1 | Manual | 只读文件保存失败 | 测试目录中 UTF-8 文件；变更可回滚；所有编辑均针对测试文件 | 打开无写权限测试文件；修改并保存 | 错误可见；不会假报成功，窗口不崩溃 |
| M07-004 | 远程编辑器 | P1 | Manual | 超大文件 | 测试目录中 UTF-8 文件；变更可回滚；所有编辑均针对测试文件 | 打开超过 4 MiB 的测试文本文件 | 明确提示超出内置编辑器限制，主窗口仍可用 |
| M07-005 | 远程编辑器 | P1 | Manual | 空文件与单行 | 测试目录中 UTF-8 文件；变更可回滚；所有编辑均针对测试文件 | 分别打开空文件、不含换行单行文件 | 空/单行状态正常，编辑保存后内容正确 |
| M07-006 | 远程编辑器 | P1 | Manual | 换行与自动换行 | 测试目录中 UTF-8 文件；变更可回滚；所有编辑均针对测试文件 | 打开含长行、CRLF 和 LF 的夹具；切换自动换行 | 编辑区可滚动，切换不丢文字，行数和光标行为合理 |
| M07-007 | 远程编辑器 | P1 | Manual | 编辑器跟随主题 | 测试目录中 UTF-8 文件；变更可回滚；所有编辑均针对测试文件 | 编辑窗口保持打开；主窗口保存深浅色主题 | 编辑器主题同步，无浅色/深色混杂导致不可读 |
| M07-008 | 远程编辑器 | P1 | Manual | 重新加载 | 测试目录中 UTF-8 文件；变更可回滚；所有编辑均针对测试文件 | 夹具侧修改已打开测试文件；在编辑器点击重新加载 | 加载最新远端内容；若存在未保存修改，行为明确且可预期 |
| M08-001 | 监控 | P0 | Manual | Demo 指标展示 | Demo、隔离 Linux /proc 与 Windows OpenSSH/CIM 夹具 | 打开 Demo 监控，等待两轮采样 | CPU、内存、网络、磁盘和进程区域可见，无渲染错误 |
| M08-002 | 监控 | P1 | Manual | Linux 指标核对 | Demo、隔离 Linux /proc 与 Windows OpenSSH/CIM 夹具 | Linux 稳定负载下记录服务器同期采样与应用显示 | 单位、总量和趋势合理；CPU在0..100%，内存使用不超过总量 |
| M08-003 | 监控 | P1 | Manual | Windows 指标核对 | Demo、隔离 Linux /proc 与 Windows OpenSSH/CIM 夹具 | 通过 Windows OpenSSH 连接；打开监控 | 显示 Windows 主机信息与 CPU/内存等，缺失计数器有明确降级 |
| M08-004 | 监控 | P0 | Manual | 暂停与继续 | Demo、隔离 Linux /proc 与 Windows OpenSSH/CIM 夹具 | 暂停后等待两个间隔；继续后等待一个间隔 | 暂停时更新时间和曲线停止；继续后刷新，不重复启动多路采样 |
| M08-005 | 监控 | P1 | Manual | 采样间隔 | Demo、隔离 Linux /proc 与 Windows OpenSSH/CIM 夹具 | 依次选择 1、2、5、10、30 秒并观察更新时间 | 刷新频率与选择一致，设置可持久化 |
| M08-006 | 监控 | P1 | Manual | 进程排序 | Demo、隔离 Linux /proc 与 Windows OpenSSH/CIM 夹具 | 分别点击 CPU、内存、PID 表头两次 | 升降序正确，进程行不串数据 |
| M08-007 | 监控 | P1 | Manual | 断线监控 | Demo、隔离 Linux /proc 与 Windows OpenSSH/CIM 夹具 | 采样时断开连接；重连并打开监控 | 显示不可用/错误，重连后可恢复，不继续显示旧数据为新采样 |
| M08-008 | 监控 | P2 | Manual | 长时间监控 | Demo、隔离 Linux /proc 与 Windows OpenSSH/CIM 夹具 | 连续运行 60 分钟并定期记录内存/采样时刻 | 历史窗口有界，内存不持续无界增长，采样间隔稳定 |
| M09-001 | 设置与本地化 | P0 | Manual | 保存与取消 | 专用 Windows 测试账户；每个测试结束恢复原偏好 | 修改主题和字号后取消；再修改后保存并重开 | 取消不应用草稿；保存持久化 |
| M09-002 | 设置与本地化 | P0 | Manual | 英语切换 | 专用 Windows 测试账户；每个测试结束恢复原偏好 | 在设置选择 English 并保存；打开设置、关于、文件和监控 | 主要界面立即使用英语，无翻译键或渲染错误 |
| M09-003 | 设置与本地化 | P0 | Manual | 中文切换 | 专用 Windows 测试账户；每个测试结束恢复原偏好 | 选择简体中文并保存；重复上述导航 | 界面立即使用中文，参数/文件名等用户数据不被翻译 |
| M09-004 | 设置与本地化 | P1 | Manual | 跟随系统 | 专用 Windows 测试账户；每个测试结束恢复原偏好 | 应用选择跟随系统语言/主题；在测试环境切换系统偏好并重启 | 按系统偏好显示，手动选择的固定模式不被覆盖 |
| M09-005 | 设置与本地化 | P1 | Manual | 字号边界 | 专用 Windows 测试账户；每个测试结束恢复原偏好 | 分别保存终端最小9和最大28字号 | 边界可选且显示正确，无文字重叠或PTY错位 |
| M09-006 | 设置与本地化 | P1 | Manual | 回滚历史选项 | 专用 Windows 测试账户；每个测试结束恢复原偏好 | 分别保存1000、100000回滚行数；输出超过1000行 | 新终端使用所选限制，回滚不无限增长 |
| M09-007 | 设置与本地化 | P1 | Manual | 隐藏文件持久化 | 专用 Windows 测试账户；每个测试结束恢复原偏好 | 关闭显示隐藏文件；保存并重启再打开文件视图 | 隐藏文件仍隐藏；开启后恢复 |
| M09-008 | 设置与本地化 | P1 | Manual | 主题重复切换 | 专用 Windows 测试账户；每个测试结束恢复原偏好 | 深色、浅色、跟随系统各切换5轮；每轮打开关于 | 主窗口和对话框一致，无旧主题残留或崩溃 |
| M09-009 | 设置与本地化 | P1 | Manual | 设置长列表滚动 | 专用 Windows 测试账户；每个测试结束恢复原偏好 | 滚动到设置底部并返回；操作文件/会话设置开关 | 各组均可达；保存/取消保持可见；无水平裁切 |
| M09-010 | 设置与本地化 | P0 | Manual | 重启后偏好 | 专用 Windows 测试账户；每个测试结束恢复原偏好 | 保存语言、主题、字体、采样间隔；关闭程序再打开 | 全部偏好保持，界面和功能一致 |
| M10-001 | 无障碍与显示 | P1 | Manual | 仅键盘导航 | 交互桌面；屏幕阅读器与显示缩放在专用测试机调整 | 只用 Tab/Shift+Tab/Enter/Esc 操作主窗口、设置、关于 | 焦点可见，主要操作均可完成，不发生焦点陷阱 |
| M10-002 | 无障碍与显示 | P1 | Manual | Esc 与对话框关闭 | 交互桌面；屏幕阅读器与显示缩放在专用测试机调整 | 分别打开设置、关于、会话编辑；按 Esc | 关闭或取消当前对话框，不触发保存，不关闭整个应用 |
| M10-003 | 无障碍与显示 | P1 | Manual | 屏幕阅读器名称 | 交互桌面；屏幕阅读器与显示缩放在专用测试机调整 | 用 Narrator 遍历图标按钮、输入框、主题选项与文件操作 | 朗读名称准确、无只读出图标编码，输入字段有标签 |
| M10-004 | 无障碍与显示 | P1 | Manual | 高对比度 | 交互桌面；屏幕阅读器与显示缩放在专用测试机调整 | 切换 Windows 高对比度；检查主要界面和弹窗 | 文本、焦点、状态可辨识；连接状态不只依赖颜色 |
| M10-005 | 无障碍与显示 | P1 | Manual | 文字缩放200% | 交互桌面；屏幕阅读器与显示缩放在专用测试机调整 | 系统文字缩放设为200%；打开设置、关于、监控 | 主要文字与按钮可达，不被固定高度裁切，长内容可滚动 |
| M10-006 | 无障碍与显示 | P1 | Manual | 显示缩放与多屏 | 交互桌面；屏幕阅读器与显示缩放在专用测试机调整 | 在100%、150%、200%DPI屏幕间移动窗口 | 图标清晰、布局缩放正确、终端光标与字符一致 |
| M10-007 | 无障碍与显示 | P1 | Manual | 最小窗口 | 交互桌面；屏幕阅读器与显示缩放在专用测试机调整 | 将窗口缩到最小支持尺寸；切换文件与监控 | 关键操作可达，表格溢出可处理，按钮不完全消失 |
| M10-008 | 无障碍与显示 | P2 | Manual | 窗口位置恢复 | 交互桌面；屏幕阅读器与显示缩放在专用测试机调整 | 移动窗口至第二屏；关闭；断开该屏后重开 | 窗口在可见显示区域内恢复，标题栏可操作 |
| M11-001 | 可靠性与数据保护 | P1 | Manual | 重复启动 | 专用测试虚拟机；备份快照；仅测试数据 | 连续启动关闭应用20次，每次打开设置和关于 | 均可启动和正常退出，无新崩溃事件 |
| M11-002 | 可靠性与数据保护 | P0 | Manual | 多会话隔离 | 专用测试虚拟机；备份快照；仅测试数据 | 同时连接两个测试主机，在终端/文件/监控间切换 | 当前标签操作只影响对应主机，不串路径、凭据或采样 |
| M11-003 | 可靠性与数据保护 | P0 | Manual | 会话关闭确认 | 专用测试虚拟机；备份快照；仅测试数据 | 已连接测试标签关闭时先取消再确认 | 取消保留连接；确认关闭连接并移除对应标签 |
| M11-004 | 可靠性与数据保护 | P1 | Manual | 传输时切换视图 | 专用测试虚拟机；备份快照；仅测试数据 | 上传大测试文件期间切换终端、监控再回文件 | 队列持续工作，回来后进度/完成状态准确 |
| M11-005 | 可靠性与数据保护 | P1 | Manual | 异常退出恢复 | 专用测试虚拟机；备份快照；仅测试数据 | 保存测试配置后，在VM内终止测试进程；重新启动 | 已成功保存的会话和设置仍可读取，无JSON半写损坏 |
| M11-006 | 可靠性与数据保护 | P0 | Manual | 凭据明文泄露 | 专用测试虚拟机；备份快照；仅测试数据 | 保存专用测试密码；检查应用日志、导出测试配置、UI与错误提示 | 不出现明文测试密码/私钥口令；密码输入遮罩，日志不泄露 |
| M11-007 | 可靠性与数据保护 | P0 | Manual | 无凭据保存连接 | 专用测试虚拟机；备份快照；仅测试数据 | 不勾选记住密码连接；退出重启后再次连接 | 再次要求输入密码，未静默持久化凭据 |
| M11-008 | 可靠性与数据保护 | P2 | Manual | 资源释放 | 专用测试虚拟机；备份快照；仅测试数据 | 重复创建关闭50个Demo标签并观察句柄/进程内存 | 连接、计时器与采样释放，资源使用趋于稳定，不持续线性增长 |
