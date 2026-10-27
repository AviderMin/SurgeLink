using SurgeLink.App.Localization;
using SurgeLink.App.Services;

namespace SurgeLink.Core.Tests;

public sealed class WorkspaceExperienceSettingsTests
{
    [Fact]
    public void FailedSettingsSaveKeepsDialogOpenAndPreservesPersistedValues()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        var store = new AppSettingsStore(path);
        store.Update(current => current with { Theme = ThemeMode.Light });
        var original = store.Current;
        var draft = original with { Theme = ThemeMode.Dark, Language = AppLanguage.ChineseSimplified };
        var failures = 0;

        using var blockedReplace = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var saved = SettingsDialogSave.TryApply(store, draft, _ => failures++);

        Assert.False(saved);
        Assert.Equal(1, failures);
        Assert.Equal(original, store.Current);
        Assert.Equal(original, new AppSettingsStore(path).Current);
    }

    [Fact]
    public void SuccessfulSettingsSaveAllowsDialogToClose()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        var store = new AppSettingsStore(path);
        var draft = store.Current with { Theme = ThemeMode.Dark, Language = AppLanguage.ChineseSimplified };
        var failures = 0;

        var saved = SettingsDialogSave.TryApply(store, draft, _ => failures++);

        Assert.True(saved);
        Assert.Equal(0, failures);
        Assert.Equal(draft, store.Current);
        Assert.Equal(draft, new AppSettingsStore(path).Current);
    }
}
