using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SurgeLink.App.Components;
using SurgeLink.App.Localization;
using SurgeLink.App.Styling;
using SurgeLink.Core.Files;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Views.Sftp;

/// <summary>
/// The modal dialogs of the file manager: create folder, rename, delete and the
/// POSIX permission editor. Each dialog is a controlled <see cref="ContentDialogElement"/>
/// whose <c>IsOpen</c> mirrors the view's dialog state, so opening and closing
/// follows the render tree instead of imperative <c>ShowAsync</c> calls.
/// </summary>
internal static class SftpDialogs
{
    private static readonly GridSize[] PermissionRowColumns = [GridSize.Px(64), GridSize.Auto, GridSize.Auto, GridSize.Auto];

    /// <summary>Create folder dialog.</summary>
    public static Element NewFolder(bool open, string name, Action<string> setName, Action commit, Action close) =>
        ContentDialog(
                Str.T("sftp.dialog.newFolder.title", "New folder"),
                VStack(
                    AppTheme.SpacingM,
                    TextBox(name, setName, placeholderText: Str.T("sftp.dialog.newFolder.placeholder", "e.g. logs"))
                        .Header(Str.T("sftp.dialog.newFolder.header", "Folder name")),
                    Caption(Str.T("sftp.dialog.newFolder.hint", "Created in the current directory."))
                        .Foreground(Theme.SecondaryText)),
                primaryButtonText: Str.T("sftp.dialog.newFolder.primary", "Create")) with
        {
            IsOpen = open,
            IsPrimaryButtonEnabled = SftpOperations.IsValidName(name),
            CloseButtonText = Str.T("sftp.dialog.cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            OnClosed = result => Handle(result, commit, close),
        };

    /// <summary>Rename dialog, pre-filled with the current name.</summary>
    public static Element Rename(bool open, RemoteFileEntry target, string name, Action<string> setName, Action commit, Action close) =>
        ContentDialog(
                Str.T("sftp.dialog.rename.title", "Rename"),
                VStack(
                    AppTheme.SpacingM,
                    TextBlock(target.FullPath).FontFamily(AppTheme.Monospace).FontSize(12).Foreground(Theme.SecondaryText),
                    TextBox(name, setName, placeholderText: Str.T("sftp.dialog.rename.placeholder", "New name"))
                        .Header(Str.T("sftp.dialog.rename.header", "Name"))),
                primaryButtonText: Str.T("sftp.dialog.rename.primary", "Rename")) with
        {
            IsOpen = open,
            IsPrimaryButtonEnabled = SftpOperations.IsValidName(name) && name != target.Name,
            CloseButtonText = Str.T("sftp.dialog.cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            OnClosed = result => Handle(result, commit, close),
        };

    /// <summary>Delete confirmation dialog; directories are deleted recursively.</summary>
    public static Element Delete(bool open, RemoteFileEntry target, Action commit, Action close) =>
        ContentDialog(
                Str.T("sftp.dialog.delete.title", "Delete"),
                VStack(
                    AppTheme.SpacingM,
                    TextBlock(target.FullPath).FontFamily(AppTheme.Monospace).FontSize(12).Foreground(Theme.SecondaryText),
                    TextBlock(target.IsDirectory
                            ? Str.T("sftp.dialog.delete.folderHint", "The folder and everything inside it will be deleted.")
                            : Str.T("sftp.dialog.delete.fileHint", "The file will be deleted."))
                        .TextWrapping(TextWrapping.Wrap)),
                primaryButtonText: Str.T("sftp.dialog.delete.primary", "Delete")) with
        {
            IsOpen = open,
            CloseButtonText = Str.T("sftp.dialog.cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
            OnClosed = result => Handle(result, commit, close),
        };

    /// <summary>POSIX permission editor: nine rwx checkboxes plus the octal preview.</summary>
    public static Element Permissions(
        bool open,
        RemoteFileEntry target,
        int mode,
        Action<int> setMode,
        Action commit,
        Action close) =>
        ContentDialog(
                Str.T("sftp.dialog.permissions.title", "Permissions"),
                VStack(
                    AppTheme.SpacingM,
                    TextBlock(target.FullPath).FontFamily(AppTheme.Monospace).FontSize(12).Foreground(Theme.SecondaryText),
                    PermissionRow(Str.T("sftp.dialog.permissions.owner", "Owner"), 0x100, 0x080, 0x040, mode, setMode),
                    PermissionRow(Str.T("sftp.dialog.permissions.group", "Group"), 0x020, 0x010, 0x008, mode, setMode),
                    PermissionRow(Str.T("sftp.dialog.permissions.others", "Others"), 0x004, 0x002, 0x001, mode, setMode),
                    Caption(DataFormat.Octal(mode) + "   "
                            + RemoteFileEntry.FormatMode(mode, target.IsDirectory))
                        .FontFamily(AppTheme.Monospace)),
                primaryButtonText: Str.T("sftp.dialog.permissions.primary", "Apply")) with
        {
            IsOpen = open,
            CloseButtonText = Str.T("sftp.dialog.cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            OnClosed = result => Handle(result, commit, close),
        };

    /// <summary>Built-in text editor for a selected remote file.</summary>
    public static Element TextEditor(
        bool open,
        SftpEditorState state,
        Action<string> setText,
        Action commit,
        Action close)
    {
        Element body = state.IsLoading
            ? Grid(
                    columns: [GridSize.Star()],
                    rows: [GridSize.Star()],
                    ProgressRing().HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center))
                .MinHeight(320)
            : VStack(
                AppTheme.SpacingM,
                TextBlock(state.Target.FullPath)
                    .FontFamily(AppTheme.Monospace)
                    .FontSize(12)
                    .Foreground(Theme.SecondaryText),
                state.Error is null
                    ? TextBox(state.Text, setText)
                        .Header(Str.T("sftp.dialog.editor.header", "File contents"))
                        .AcceptsReturn(true)
                        .TextWrapping(TextWrapping.Wrap)
                        .FontFamily(AppTheme.Monospace)
                        .MinHeight(340)
                        .MaxHeight(520)
                        .Set(box => box.IsSpellCheckEnabled = false)
                    : InfoBar(
                        Str.T("sftp.dialog.editor.error", "Could not read the file"),
                        state.Error)
                        .Severity(InfoBarSeverity.Error)
                        .IsClosable(false),
                state.Error is null
                    ? Caption(Str.T("sftp.dialog.editor.hint", "Changes are written back to the remote file when you save."))
                        .Foreground(Theme.SecondaryText)
                    : null);

        return ContentDialog(
                Str.T("sftp.dialog.editor.title", "Edit file"),
                body.Width(640),
                primaryButtonText: Str.T("sftp.dialog.editor.primary", "Save")) with
        {
            IsOpen = open,
            IsPrimaryButtonEnabled = !state.IsLoading && !state.IsSaving && state.Error is null,
            CloseButtonText = Str.T("sftp.dialog.cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            OnClosed = result => Handle(result, commit, close),
        };
    }

    private static Element PermissionRow(string label, int readBit, int writeBit, int execBit, int mode, Action<int> setMode) =>
        Grid(
            columns: PermissionRowColumns,
            rows: [GridSize.Auto],
            Caption(label).Width(56).VAlign(VerticalAlignment.Center).Grid(row: 0, column: 0),
            PermissionToggle(Str.T("sftp.dialog.permissions.read", "Read"), readBit, mode, setMode).Grid(row: 0, column: 1),
            PermissionToggle(Str.T("sftp.dialog.permissions.write", "Write"), writeBit, mode, setMode).Grid(row: 0, column: 2),
            PermissionToggle(Str.T("sftp.dialog.permissions.execute", "Execute"), execBit, mode, setMode).Grid(row: 0, column: 3));

    private static Element PermissionToggle(string label, int bit, int mode, Action<int> setMode) =>
        CheckBox(
            Optional<bool?>.Of((mode & bit) != 0),
            onIsCheckedChanged: _ => setMode(mode ^ bit),
            label: label);

    private static void Handle(ContentDialogResult result, Action commit, Action close)
    {
        if (result == ContentDialogResult.Primary)
        {
            commit();
        }
        else
        {
            close();
        }
    }
}
