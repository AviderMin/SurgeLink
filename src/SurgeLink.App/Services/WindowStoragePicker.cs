using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace SurgeLink.App.Services;

/// <summary>Storage pickers bound to a specific window, independent of Reactor's ambient render host.</summary>
internal sealed class WindowStoragePicker(Window owner)
{
    public static Context<WindowStoragePicker?> Context { get; } = new(null);

    private bool _isPicking;

    public async Task<StorageFile?> PickFileAsync(
        IReadOnlyList<string>? fileTypes = null,
        string? commitButtonText = null)
    {
        EnsureUiThread();
        if (_isPicking) { return null; }
        _isPicking = true;
        try
        {
            var picker = new FileOpenPicker();
            foreach (var type in fileTypes ?? ["*"])
            {
                picker.FileTypeFilter.Add(type);
            }
            if (!string.IsNullOrEmpty(commitButtonText)) { picker.CommitButtonText = commitButtonText; }
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
            return await picker.PickSingleFileAsync();
        }
        finally { _isPicking = false; }
    }

    public async Task<StorageFolder?> PickFolderAsync()
    {
        EnsureUiThread();
        if (_isPicking) { return null; }
        _isPicking = true;
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
            return await picker.PickSingleFolderAsync();
        }
        finally { _isPicking = false; }
    }

    private void EnsureUiThread()
    {
        if (!owner.DispatcherQueue.HasThreadAccess)
        {
            throw new InvalidOperationException("Storage pickers must be opened on their owning window's UI thread.");
        }
    }
}
