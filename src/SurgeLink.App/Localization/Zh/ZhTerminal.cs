namespace SurgeLink.App.Localization.Zh;

/// <summary>Chinese strings for the terminal surface (context menu, automation names).</summary>
public static class ZhTerminal
{
    /// <summary>Key -> 中文 mapping. Keep keys prefixed with <c>terminal.</c>.</summary>
    public static IReadOnlyDictionary<string, string> Map { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["terminal.copy"] = "复制",
        ["terminal.paste"] = "粘贴",
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
