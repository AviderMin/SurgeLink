# 代码、UI 一致性与健壮性验证

本次对当前工作区运行全套自动化测试、构建、静态检查，并抽查实际 WinUI 窗口。
保留了开始检查前已有的 5 个文件改动；以下新增修复与测试尚未提交。
本报告记录验证范围与证据，不代表所有执行路径、平台和外部设备均已验证。

## 结果

| 检查 | 结果 |
| --- | --- |
| 全解决方案 x64 Debug，警告视为错误 | 通过，0 警告、0 错误 |
| 应用 x64 Release，警告视为错误 | 通过，0 警告、0 错误 |
| 应用 ARM64 Release 交叉编译，警告视为错误 | 通过，0 警告、0 错误 |
| Release 全套测试，串行运行 | 941 通过、0 失败、0 跳过 |
| UI 本地化静态检查 | 477 处字面量调用、485 条翻译，0 问题 |
| NuGet 直接与传递依赖漏洞扫描 | 未报告已知漏洞 |
| 仓库疑似密钥与敏感文件名扫描 | 未发现匹配项；该扫描使用启发式规则 |
| 发布说明 Python 回归测试 | 1 项通过 |
| Release Tag PowerShell 回归检查 | 5 个场景通过 |
| Git diff 空白检查 | 通过 |

测试分布：Core 229、Terminal 285、SSH 321、Telnet 16、Serial 32、Conformance 58。
本次新增 23 个测试用例，覆盖磁盘写入失败、损坏数据、非法设置、订阅者异常及队列关闭。

## 发现并修复

1. **存储写入失败导致内存与磁盘不同步。** 会话、凭据和主机指纹存储原先先改内存，
   后写文件。现在构建候选快照，写入成功后才发布到内存。8 个失败场景测试先复现问题，
   修复后全部通过。
2. **损坏数据中的空条目可导致启动或读取异常。** 会话列表排除空条目与关键字段为空的
   资料，并恢复空的协议选项集合；凭据恢复空字典、排除空值；主机指纹忽略无效条目。
   5 个损坏数据测试通过。
3. **界面保存失败可能让异常逃出 UI 回调。** 设置、会话保存、删除及演示入口现在捕获
   预期的存储错误并显示统一的错误 InfoBar。失败时不继续发起连接；独立文件写入部分
   成功时重新同步会话列表。实际设置写入失败已在运行窗口中验证：显示错误，应用保持响应，
   原主题未写入失败的更改。
4. **队列关闭遗漏排队任务的完成通知。** 排队任务被移除时现在收到一次取消完成通知，
   且不会访问远程文件；补充测试也确认异常订阅者不会阻止其他订阅者或后续传输。

额外补充了设置存储的 8 个边界测试，并新增 `tools/check-ui-localization.py`，用于检查
字面量翻译键、重复键及中英文格式参数。更新了开发验证文档。

## 实际 UI 检查范围

使用 computer-use 技能操作源码构建的窗口，与已安装版本区分。

- 中文、跟随系统的浅色界面：空状态、离线 Demo 连接、终端、文件列表、监控卡片、设置弹窗。
- 切换到英文和深色主题：会话保持连接，终端／文件／监控导航及监控文案更新。
- 英文深色的新建会话弹窗：分组、滚动内容与固定底部按钮正常；空主机保存显示校验信息，
  表单保持打开，不生成会话。
- 在临时阻断设置写入后尝试保存：统一错误提示正常呈现，进程保持响应，没有新增崩溃日志。
- 上述观察未出现 Reactor 的 `Render error` 页面。

设置文件已按原始字节恢复，并通过 SHA-256 比对；临时写入阻断目录已清理，测试实例已关闭。
演示入口会更新已有 Demo 资料的最近使用信息。

## 验证边界与后续风险

- 一次并行测试中的 ANSI 性能测试耗时 200.8 ms，超过现有 200 ms 阈值；初始运行与最终
  串行运行均通过。未放宽测试阈值，此项仍对宿主机负载敏感。
- 实际 UI 为主要路径抽查；最小窗口尺寸、不同 DPI、高对比度及完整键盘／屏幕阅读器流程
  未完成验证。窗口缩小的自动化尝试未成功改变尺寸，因此不据此声称适配通过。
- 未连接用户的 SSH 主机；SSH 认证、实际 SFTP/SCP、主机密钥变化及真实网络中断仍需集成环境。
  Telnet 使用回环服务器测试，Serial 使用模拟设备，未验证真实 COM 硬件。
- ARM64 仅交叉编译；Windows 10 19044 及 ARM64 设备运行、安装包安装未验证。
- 会话文件与凭据文件仍是独立写入，不是跨文件事务。某个文件成功、另一个失败时现在能报告
  错误并同步 UI，但不保证两个文件同时提交或回滚。
- 本地化静态脚本只覆盖字面量键和字面量英文回退值；动态键需要运行验证。

## 复现与证据

```powershell
dotnet build SurgeLink.slnx -c Debug -p:Platform=x64 -warnaserror
dotnet build src/SurgeLink.App -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -warnaserror
dotnet build src/SurgeLink.App -c Release -p:Platform=ARM64 -p:RuntimeIdentifier=win-arm64 -warnaserror
dotnet test SurgeLink.slnx -c Release -p:Platform=x64 -m:1
python tools/check-ui-localization.py
python tools/check-repository.py
dotnet list SurgeLink.slnx package --vulnerable --include-transitive --format json
python -m unittest discover -s tools/tests -p test_release_notes.py -v
pwsh -NoProfile -File tools/tests/test-release-tag.ps1
git diff --check
```

本地原始证据位于忽略目录 `artifacts/verification/`：
`build-debug.log`、`build-release-x64.log`、`build-arm64.log`、`tests-release.log`、
`serial-tests/*.trx` 和 `dependencies.json`。
