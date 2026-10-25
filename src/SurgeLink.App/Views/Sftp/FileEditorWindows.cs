using System.Runtime.CompilerServices;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using SurgeLink.App.Localization;
using SurgeLink.App.Services;
using SurgeLink.Core.Files;
using SurgeLink.Core.Sessions;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Views.Sftp;

/// <summary>One editor per remote file and live file-system instance.</summary>
internal static class FileEditorWindows
{
    private static readonly ConditionalWeakTable<IRemoteFileSystem, Dictionary<string, ReactorWindow>> Windows = new();

    public static void Open(IRemoteFileSystem fileSystem, RemoteFileEntry target,
        IAppSettingsStore settings, IProtocolSession? session)
    {
        RemoteFileAccess.RequireRegularFile(target);
        var editors = Windows.GetOrCreateValue(fileSystem);
        if (editors.TryGetValue(target.FullPath, out var existing))
        {
            existing.Show();
            existing.Activate();
            return;
        }

        var editor = ReactorApp.OpenWindow(new WindowSpec
        {
            Title = Str.F("sftp.editor.windowTitle", "{0} — Text editor", target.Name),
            Width = 1000,
            Height = 720,
            MinWidth = 640,
            MinHeight = 480,
            // A document editor is a normal top-level window, not an owned dialog.
            // Keep the standard caption buttons and use the active display for placement.
            Style = WindowStyle.Default,
            ResizeMode = WindowResizeMode.CanResize,
            IsMinimizable = true,
            IsMaximizable = true,
            ShowInTaskbar = true,
            ExtendsContentIntoTitleBar = true,
            StartPosition = WindowStartPosition.CenterOnCurrent,
            Backdrop = BackdropChoice.Of(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)
                ? BackdropKind.MicaAlt : BackdropKind.None),
        }, _ => Component<FileEditorWindow, FileEditorWindowProps>(new(fileSystem, target, settings, session)));
        editors.Add(target.FullPath, editor);
        editor.Closed += (_, _) => editors.Remove(target.FullPath);
    }
}
