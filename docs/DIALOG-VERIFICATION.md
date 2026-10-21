# 设置与关于对话框运行验证

日期：2026-10-21。使用独立 x64 构建，通过 computer-use 的真实桌面截图与 UI Automation 验证。

## 发现并修复

- 保存深色主题后，主窗口变为深色，但 ContentDialog 弹出层仍使用浅色。现在设置、关于均订阅实际设置，并直接设置原生 ContentDialog.RequestedTheme。草稿主题在保存前不生效。
- 首次或重复打开时，原生按钮模板可能晚于 Opened 回调创建，导致按钮自动化 ID 回退为 PrimaryButton / CloseButton。增加模板布局完成后的幂等标记，复验得到 SettingsPrimary、SettingsClose、AboutPrimary。

## 已验证

- 设置：中文标题、说明、卡片、控件与固定操作区正常显示；内容可滚动到文件和会话设置，操作按钮保持可见。
- 设置：修改主题并保存后主窗口生效，重新打开仍保留；选择另一主题后取消不应用草稿。
- 关于：产品名、版本 0.1.0 和说明完整显示；关闭按钮、Esc 均可关闭。
- 设置和关于：深色主题实际截图检查通过；浅色布局也已检查。
- 交替、重复打开设置和关于后，按钮标识正确，主窗口可继续操作，没有 Render error 或进程退出。
- 完成后已通过设置 UI 将主题恢复为原先的“跟随系统”，并确认主窗口恢复浅色。
- 最终构建 0 警告、0 错误；git diff --check 通过。

最终构建命令：

```powershell
dotnet build src/SurgeLink.App/SurgeLink.App.csproj --no-restore -p:Platform=x64 -p:UseSharedCompilation=false -p:OutDir=D:/Projects/Github/SurgeLink/artifacts/dialog-theme-verified/ -m:1 -v:minimal
```

本次未覆盖系统高对比度、系统文字缩放、屏幕阅读器朗读、英文界面及最小窗口尺寸。没有更改这些系统设置。
