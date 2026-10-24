using SurgeLink.App.Localization;
using SurgeLink.App.Services;

namespace SurgeLink.Core.Tests;

public sealed class AppSettingsStoreTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("{invalid")]
    [InlineData("[]")]
    public void CorruptSettingsFallBackToDefaults(string json)
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        File.WriteAllText(path, json);
        Assert.Equal(AppSettings.Default, new AppSettingsStore(path).Current);
    }

    [Fact]
    public void InvalidPersistedValuesAreNormalizedBeforeTheyReachControls()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        File.WriteAllText(path, """
            {"theme":99,"language":99,"terminalFontFamily":null,
             "terminalFontSize":-1,"terminalScrollbackLines":2147483647,
             "monitorIntervalSeconds":0,"terminalPalette":null}
            """);
        var settings = new AppSettingsStore(path).Current;
        Assert.Equal(ThemeMode.System, settings.Theme);
        Assert.Equal(AppLanguage.System, settings.Language);
        Assert.Equal(string.Empty, settings.TerminalFontFamily);
        Assert.Equal(9, settings.TerminalFontSize);
        Assert.Equal(100000, settings.TerminalScrollbackLines);
        Assert.Equal(1, settings.MonitorIntervalSeconds);
        Assert.Equal(AppSettings.Default.TerminalPalette, settings.TerminalPalette);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteFontSizesCanBeSavedSafely(double fontSize)
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        var store = new AppSettingsStore(path);
        store.Update(current => current with { TerminalFontSize = fontSize, Language = AppLanguage.English });
        Assert.Equal(AppSettings.Default.TerminalFontSize, store.Current.TerminalFontSize);
        Assert.Equal(store.Current, new AppSettingsStore(path).Current);
    }

    [Fact]
    public void FailedWriteDoesNotChangeSettingsOrPublishSuccess()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        var store = new AppSettingsStore(path);
        store.Update(current => current with { Theme = ThemeMode.Light });
        var original = store.Current;
        var changes = 0;
        store.Changed += (_, _) => changes++;
        using var blockedReplace = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var failure = Record.Exception(() => store.Update(current => current with { Theme = ThemeMode.Dark }));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(original, store.Current);
        Assert.Equal(original, new AppSettingsStore(path).Current);
        Assert.Equal(0, changes);
    }
}
