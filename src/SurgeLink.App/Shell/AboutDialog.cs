using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using SurgeLink.App.Localization;
using SurgeLink.App.Styling;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Shell;

internal sealed record AboutDialogProps(Action OnClosed);

/// <summary>Product information using the same native dialog presentation as Settings.</summary>
internal sealed class AboutDialog : Component<AboutDialogProps>
{
    public override Element Render() => AppDialog.Render(
        Str.T("app.aboutTitle", "About SurgeLink"),
        VStack(AppTheme.SpacingXl,
            VStack(AppTheme.SpacingS,
                Subtitle("SurgeLink"),
                Caption(Str.F("app.aboutVersion", "Version {0}",
                    typeof(AboutDialog).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"))
                    .Foreground(Theme.SecondaryText)),
            Card(Body(Str.T("app.aboutBody",
                    "A multi-protocol terminal for Windows. Terminal, SFTP and remote monitoring, built with WinUI 3 + Reactor."))
                .TextWrapping(TextWrapping.Wrap))),
        "About",
        _ => Props.OnClosed(),
        Str.T("common.close", "Close"));
}
