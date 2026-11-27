namespace SurgeLink.App.Services;

/// <summary>Applies a staged settings draft without discarding it when persistence fails.</summary>
public static class SettingsDialogSave
{
    /// <summary>Returns whether the dialog may close after a save attempt.</summary>
    public static bool TryApply(IAppSettingsStore store, AppSettings draft, Action<Exception> onSaveFailed) =>
        TryApply(store, store.Current, draft, onSaveFailed);

    /// <summary>Applies only fields changed relative to <paramref name="initial"/> onto the latest store state.</summary>
    public static bool TryApply(IAppSettingsStore store, AppSettings initial, AppSettings draft, Action<Exception> onSaveFailed)
    {
        try
        {
            store.Update(basis => MergeChangedFields(basis, initial, draft));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onSaveFailed(ex);
            return false;
        }
    }

    internal static AppSettings MergeChangedFields(AppSettings basis, AppSettings initial, AppSettings draft) => basis with
    {
        Theme = draft.Theme != initial.Theme ? draft.Theme : basis.Theme,
        Language = draft.Language != initial.Language ? draft.Language : basis.Language,
        TerminalFontFamily = !string.Equals(draft.TerminalFontFamily, initial.TerminalFontFamily, StringComparison.Ordinal)
            ? draft.TerminalFontFamily : basis.TerminalFontFamily,
        TerminalFontSize = !draft.TerminalFontSize.Equals(initial.TerminalFontSize)
            ? draft.TerminalFontSize : basis.TerminalFontSize,
        TerminalPalette = !string.Equals(draft.TerminalPalette, initial.TerminalPalette, StringComparison.Ordinal)
            ? draft.TerminalPalette : basis.TerminalPalette,
        TerminalScrollbackLines = draft.TerminalScrollbackLines != initial.TerminalScrollbackLines
            ? draft.TerminalScrollbackLines : basis.TerminalScrollbackLines,
        ConfirmCloseConnectedTab = draft.ConfirmCloseConnectedTab != initial.ConfirmCloseConnectedTab
            ? draft.ConfirmCloseConnectedTab : basis.ConfirmCloseConnectedTab,
        MonitorIntervalSeconds = draft.MonitorIntervalSeconds != initial.MonitorIntervalSeconds
            ? draft.MonitorIntervalSeconds : basis.MonitorIntervalSeconds,
        ShowHiddenFiles = draft.ShowHiddenFiles != initial.ShowHiddenFiles
            ? draft.ShowHiddenFiles : basis.ShowHiddenFiles,
        PrivacyMode = draft.PrivacyMode != initial.PrivacyMode
            ? draft.PrivacyMode : basis.PrivacyMode,
    };
}
