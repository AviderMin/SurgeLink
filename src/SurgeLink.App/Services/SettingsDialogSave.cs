namespace SurgeLink.App.Services;

/// <summary>Applies a staged settings draft without discarding it when persistence fails.</summary>
public static class SettingsDialogSave
{
    /// <summary>Returns whether the dialog may close after a save attempt.</summary>
    public static bool TryApply(IAppSettingsStore store, AppSettings draft, Action<Exception> onSaveFailed)
    {
        try
        {
            store.Update(_ => draft);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onSaveFailed(ex);
            return false;
        }
    }
}
