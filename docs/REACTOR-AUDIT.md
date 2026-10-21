# WinUI 3 / Reactor 审查记录

审查日期：2026-10-21。代码基线使用 Microsoft.UI.Reactor 0.1.0-preview.15、Windows App SDK 2.1.3。

## 结论与依据

项目整体符合 Reactor 的声明式组件方向：通过 `ReactorApp` 启动，使用 `Component<TProps>`、record props、Hooks、外部状态仓库及原生 WinUI 控件。主窗口已有 TitleBar、Mica、星号行布局，主要界面使用主题令牌、自动化名称和本地化资源。

本次以官方 [项目说明](https://github.com/microsoft/microsoft-ui-reactor)、[设计指南](https://github.com/microsoft/microsoft-ui-reactor/blob/main/skills/design.md)、[Hooks 与列表规则](https://github.com/microsoft/microsoft-ui-reactor/blob/main/SKILL.md) 及本机已安装版本的 API 索引为依据。Reactor 是实验性框架；这些检查是代码审查，不代表正式的 Windows 设计或无障碍认证。官方允许与原生控件互操作，因此终端的 `XamlHostElement` 本身不是违规。

## 已修复的问题

| 位置 | 问题及修复 |
| --- | --- |
| SftpView | 目录加载在 `Task.Run` 内调用 `setCwd`、`setSelected`，但它们没有启用线程安全。改用 `UseState(..., threadSafe: true)`，与同一任务中的其他状态更新保持一致。 |
| SftpView、MonitorView、SidebarMonitor | 使用本地状态复制设置仓库，再通过 Effect 手动订阅；读取与订阅间存在更新遗漏窗口，也无法安全处理任意线程的通知。统一使用 `UseExternalStore`。 |
| FileEditorWindow / SftpView | 独立窗口没有订阅和应用用户主题，可能与主窗口不一致。显式传入设置仓库，订阅主题并应用到编辑器根节点；辅助窗口使用 MicaAlt。 |
| FileEditorWindow | 底栏在 HStack 中对状态文字使用省略号，主轴无限测量使长错误文本无法正确裁剪。改用 `Star / Auto / Auto` 列 Grid，并提供完整文字 ToolTip。 |
| MonitorProcessTable | `UseMemo` 读取 `MaxRows`，却未将它列为依赖，单独修改行数上限不会重算。补齐依赖。 |
| LineChart | 动态网格线和曲线没有 Key。为网格位置、序列槽位添加独立 Key，避免开关网格或空序列出现时按位置错误复用子项。序列身份按当前 API 的颜色槽位定义。 |
| Gauge | 包含文字的容器使用固定 Height，放大文字时可能被高度约束。改为 MinHeight，保留圆环尺寸并允许容器增长。 |

## 验证范围

- 应用 x64 构建通过，包含所引用 Reactor 包的分析器检查，0 警告、0 错误。
- 构建命令：`dotnet build src/SurgeLink.App/SurgeLink.App.csproj --no-restore -p:Platform=x64 -p:UseSharedCompilation=false -m:1 -v:minimal`。
- `dotnet test SurgeLink.slnx --no-restore -p:UseSharedCompilation=false -m:1 -v:minimal` 在沙箱外通过：Core 102、协议一致性 30、SSH 316、终端 273，共 721 项，无失败或跳过。沙箱内运行遇到 testhost 连接超时，沙箱外完整重跑成功。这些测试不覆盖本次调整的真实 WinUI 视觉布局。
- 本次没有运行交互式 UI 验证，浅色 / 深色 / Windows 高对比度、200% 文字缩放、屏幕阅读器及窗口缩放效果仍需运行时验证。主题令牌与自动化名称的存在不能替代这些验证。

## 后续审查规则

- 后台工作更新 Hooks 状态时，使用线程安全更新器；共享仓库用 `UseExternalStore`。
- `UseMemo` 依赖必须包含计算中读取的可变 props。
- 新窗口显式处理主题设置，不依赖主窗口的视觉继承。
- 需要省略文字的横向区域使用受约束的 Grid 星号列；包含文字的容器优先使用 MinHeight。
- 动态子项设置稳定 Key；图表中序列重排若需要保留业务身份，应扩展 props 提供唯一序列 ID。
