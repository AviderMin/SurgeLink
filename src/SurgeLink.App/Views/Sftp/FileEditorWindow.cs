using System.Text;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SurgeLink.App.Localization;
using SurgeLink.App.Styling;
using SurgeLink.Core.Files;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Views.Sftp;

/// <summary>Props for the standalone text editor window.</summary>
internal sealed record FileEditorWindowProps(IRemoteFileSystem FileSystem, RemoteFileEntry Target);

/// <summary>
/// A standalone editor window for one remote text file. The window owns its
/// editing state, reads through the session's file system capability and writes
/// back through the same SFTP/SCP path, so no UI-thread file I/O is performed.
/// </summary>
internal sealed class FileEditorWindow : Component<FileEditorWindowProps>
{
    private const int MaximumEditorBytes = 4 * 1024 * 1024;

    public override Element Render()
    {
        var props = Props;
        var target = props.Target;
        var (text, setText) = UseState(string.Empty, threadSafe: true);
        var (loading, setLoading) = UseState(true, threadSafe: true);
        var (saving, setSaving) = UseState(false, threadSafe: true);
        var (dirty, setDirty) = UseState(false, threadSafe: true);
        var (error, setError) = UseState<string?>(null, threadSafe: true);
        var (status, setStatus) = UseState<string?>(null, threadSafe: true);
        var (wrap, setWrap) = UseState(true);

        UseEffect(
            () =>
            {
                var cancellation = new CancellationTokenSource();
                _ = LoadAsync(cancellation.Token);
                return () => cancellation.Cancel();
            },
            props.FileSystem,
            target.FullPath);

        async Task LoadAsync(CancellationToken cancellationToken)
        {
            setLoading(true);
            setError(null);
            setStatus(null);
            try
            {
                if (target.Size > MaximumEditorBytes)
                {
                    throw new InvalidOperationException(
                        Str.T("sftp.editor.tooLarge", "This file is too large for the built-in text editor."));
                }

                await using var stream = await props.FileSystem
                    .OpenReadAsync(target.FullPath, cancellationToken)
                    .ConfigureAwait(true);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                setText(content);
                setDirty(false);
                setLoading(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                setError(exception.Message);
                setLoading(false);
            }
        }

        async Task SaveAsync()
        {
            if (loading || saving || error is not null)
            {
                return;
            }

            setSaving(true);
            setError(null);
            setStatus(null);
            try
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
                await using (var stream = await props.FileSystem
                                 .OpenWriteAsync(
                                     target.FullPath,
                                     bytes.Length,
                                     progress: null,
                                     CancellationToken.None)
                                 .ConfigureAwait(true))
                {
                    await stream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(true);
                    await stream.FlushAsync(CancellationToken.None).ConfigureAwait(true);
                }

                setDirty(false);
                setSaving(false);
                setStatus(Str.T("sftp.editor.saved", "Saved"));
            }
            catch (Exception exception)
            {
                setError(exception.Message);
                setSaving(false);
            }
        }

        void Reload()
        {
            if (!loading && !saving)
            {
                _ = LoadAsync(CancellationToken.None);
            }
        }

        var lineCount = text.Count(character => character == '\n') + 1;
        var characterCount = text.Length;
        var statusText = error ?? (loading
            ? Str.T("sftp.editor.loading", "Loading file…")
            : saving
                ? Str.T("sftp.editor.saving", "Saving…")
                : dirty
                    ? Str.T("sftp.editor.unsaved", "Unsaved changes")
                    : status ?? Str.T("sftp.editor.ready", "Ready"));

        var header = Grid(
            columns: [GridSize.Star(), GridSize.Auto],
            rows: [GridSize.Auto],
            VStack(
                    AppTheme.SpacingXs,
                    BodyStrong(target.Name).TextTrimming(TextTrimming.CharacterEllipsis),
                    TextBlock(target.FullPath)
                        .FontFamily(AppTheme.Monospace)
                        .FontSize(12)
                        .Foreground(Theme.SecondaryText)
                        .TextTrimming(TextTrimming.CharacterEllipsis))
                .VAlign(VerticalAlignment.Center)
                .Grid(row: 0, column: 0),
            HStack(
                    AppTheme.SpacingS,
                    ToggleButton(Str.T("sftp.editor.wordWrap", "Word wrap"), wrap, setWrap)
                        .AutomationName(Str.T("sftp.editor.wordWrap", "Word wrap")),
                    Button(Str.T("sftp.editor.reload", "Reload"), Reload)
                        .SubtleButton()
                        .IsEnabled(!loading && !saving)
                        .AutomationName(Str.T("sftp.editor.reload", "Reload")),
                    Button(Str.T("sftp.editor.save", "Save"), () => _ = SaveAsync())
                        .AccentButton()
                        .IsEnabled(!loading && !saving && error is null)
                        .AutomationName(Str.T("sftp.editor.save", "Save")))
                .VAlign(VerticalAlignment.Center)
                .Grid(row: 0, column: 1))
            .Padding(AppTheme.SpacingL, AppTheme.SpacingM, AppTheme.SpacingL, AppTheme.SpacingS);

        Element body;
        if (loading)
        {
            body = Grid(
                    columns: [GridSize.Star()],
                    rows: [GridSize.Star()],
                    ProgressRing().HAlign(HorizontalAlignment.Center).VAlign(VerticalAlignment.Center))
                .MinHeight(240);
        }
        else if (error is not null)
        {
            body = InfoBar(Str.T("sftp.editor.openFailed", "Could not open the file"), error)
                .Severity(InfoBarSeverity.Error)
                .IsClosable(false)
                .VAlign(VerticalAlignment.Top);
        }
        else
        {
            body = Border(
                    TextBox(text, value =>
                        {
                            setText(value);
                            setDirty(true);
                            setStatus(null);
                        })
                        .AcceptsReturn(true)
                        .TextWrapping(wrap ? TextWrapping.Wrap : TextWrapping.NoWrap)
                        .FontFamily(AppTheme.Monospace)
                        .FontSize(14)
                        .AutomationName(Str.T("sftp.editor.contents", "File contents"))
                        .HAlign(HorizontalAlignment.Stretch)
                        .VAlign(VerticalAlignment.Stretch)
                        .Set(box => box.IsSpellCheckEnabled = false))
                .Padding(AppTheme.SpacingM)
                .Background(Theme.CardBackground)
                .WithBorder(Theme.CardStroke)
                .CornerRadius(AppTheme.CardRadius);
        }

        var footer = HStack(
                AppTheme.SpacingL,
                TextBlock(statusText)
                    .Foreground(error is null ? Theme.SecondaryText : Theme.SystemCritical)
                    .TextTrimming(TextTrimming.CharacterEllipsis),
                TextBlock(Str.F("sftp.editor.lines", "{0} lines", lineCount))
                    .Foreground(Theme.SecondaryText),
                TextBlock(Str.F("sftp.editor.characters", "{0} characters", characterCount))
                    .Foreground(Theme.SecondaryText))
            .Padding(AppTheme.SpacingL, AppTheme.SpacingS, AppTheme.SpacingL, AppTheme.SpacingM)
            .VAlign(VerticalAlignment.Center);

        return Grid(
            columns: [GridSize.Star()],
            rows: [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            header.Grid(row: 0),
            body.Grid(row: 1),
            footer.Grid(row: 2));
    }
}
