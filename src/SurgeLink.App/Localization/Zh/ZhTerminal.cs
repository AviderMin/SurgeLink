namespace SurgeLink.App.Localization.Zh;

/// <summary>Chinese strings for the terminal surface (context menu, automation names).</summary>
public static class ZhTerminal
{
    /// <summary>Key -> 中文 mapping. Keep keys prefixed with <c>terminal.</c>.</summary>
    public static IReadOnlyDictionary<string, string> Map { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["terminal.reopenFailed"] = "无法重新打开 Shell",
        ["terminal.shellExited"] = "远程 Shell 已退出，SSH 连接仍在线。",
        ["terminal.connectionClosed"] = "终端已关闭。",
        ["terminal.reopen"] = "重新打开 Shell",
        ["shell.status.shellExited"] = "已连接 · Shell 已退出",
        ["terminal.find"] = "查找…",
        ["terminal.find.placeholder"] = "在终端中查找",
        ["terminal.find.previous"] = "上一个匹配（Shift+Enter）",
        ["terminal.find.next"] = "下一个匹配（Enter）",
        ["terminal.find.close"] = "关闭查找（Esc）",
        ["terminal.find.case"] = "区分大小写",
        ["terminal.find.none"] = "没有匹配",
        ["terminal.find.position"] = "第 {0} 个，共 {1} 个",
        ["terminal.find.positionCapped"] = "第 {0} 个，前 {1} 个匹配",
        ["terminal.copy"] = "复制",
        ["terminal.paste"] = "粘贴",
        ["terminal.remoteClipboard"] = "允许此 Shell 写入剪贴板",
        ["terminal.selectAll"] = "全选",
        ["terminal.clear"] = "清屏",
        ["terminal.zoomIn"] = "放大字号",
        ["terminal.zoomOut"] = "缩小字号",
        ["terminal.resetZoom"] = "重置字号",
        ["terminal.output"] = "终端输出",
        ["terminal.label"] = "终端",
        ["terminal.unavailable"] = "此会话不支持终端。",
    };
}
