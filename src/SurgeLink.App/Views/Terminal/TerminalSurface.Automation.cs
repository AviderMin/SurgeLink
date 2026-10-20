using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

using SurgeLink.App.Localization;

namespace SurgeLink.App.Views.Terminal;

/// <summary>
/// Exposes the visible screen text as the automation name and value, which is what
/// screen readers read and what the team's UIA dumps capture. Values are read live
/// from the emulator, so a dump always shows the current screen.
/// </summary>
internal sealed class TerminalSurfaceAutomationPeer : FrameworkElementAutomationPeer, IValueProvider
{
    internal TerminalSurfaceAutomationPeer(TerminalSurface owner)
        : base(owner)
    {
    }

    private string VisibleText => ((TerminalSurface)Owner).GetAccessibleText();

    bool IValueProvider.IsReadOnly => true;

    string IValueProvider.Value => VisibleText;

    void IValueProvider.SetValue(string value) =>
        throw new InvalidOperationException("The terminal is a read-only text surface.");

    /// <inheritdoc />
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;

    /// <inheritdoc />
    protected override string GetClassNameCore() => "TerminalSurface";

    /// <inheritdoc />
    protected override string GetNameCore()
    {
        string text = VisibleText;
        return string.IsNullOrEmpty(text) ? Str.T("terminal.label", "Terminal") : text;
    }

    /// <inheritdoc />
    protected override object GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Value ? this : base.GetPatternCore(patternInterface);
}

/// <summary>Automation plumbing for <see cref="TerminalSurface"/>.</summary>
internal sealed partial class TerminalSurface
{
    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new TerminalSurfaceAutomationPeer(this);
}