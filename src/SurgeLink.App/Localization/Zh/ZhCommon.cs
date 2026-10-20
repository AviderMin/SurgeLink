namespace SurgeLink.App.Localization.Zh;

/// <summary>Shared Chinese strings (app level, common verbs).</summary>
public static class ZhCommon
{
    /// <summary>Key -> 中文 mapping.</summary>
    public static IReadOnlyDictionary<string, string> Map { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["common.ok"] = "确定",
        ["common.cancel"] = "取消",
        ["common.close"] = "关闭",
        ["common.save"] = "保存",
        ["common.delete"] = "删除",
        ["common.rename"] = "重命名",
        ["common.refresh"] = "刷新",
        ["common.retry"] = "重试",
        ["common.connect"] = "连接",
        ["common.disconnect"] = "断开",
        ["app.name"] = "SurgeLink",
        ["app.tagline"] = "多协议终端",
        ["app.about"] = "About SurgeLink",
        ["app.aboutTitle"] = "关于 SurgeLink",
        ["app.aboutBody"] = "面向 Windows 的多协议终端。终端、SFTP 与远程监控，基于 WinUI 3 + Reactor 构建。",
    };
}
