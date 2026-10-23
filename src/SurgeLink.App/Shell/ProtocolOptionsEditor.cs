using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using SurgeLink.App.Localization;
using SurgeLink.App.Styling;
using SurgeLink.Core.Protocols;
using static Microsoft.UI.Reactor.Factories;

namespace SurgeLink.App.Shell;

internal sealed record ProtocolOptionsEditorProps(
    IReadOnlyList<ProtocolOptionDescriptor> Fields,
    IReadOnlyDictionary<string, string> Values,
    Action<string, string> OnChange);

/// <summary>Renders protocol-owned settings without referencing any transport implementation.</summary>
internal sealed class ProtocolOptionsEditor : Component<ProtocolOptionsEditorProps>
{
    public override Element Render() => VStack(AppTheme.SpacingL, Props.Fields.Select(field => BuildField(field).WithKey(field.Key)).ToArray());

    private Element BuildField(ProtocolOptionDescriptor field)
    {
        var props = Props;
        string value = props.Values.TryGetValue(field.Key, out string? configured) ? configured : field.DefaultValue;
        string label = Str.T("protocol.option." + field.Key, field.Label);
        void Change(string next) => props.OnChange(field.Key, next);
        if (field.IsBoolean)
            return CheckBox(Optional<bool?>.Of(bool.TryParse(value, out bool enabled) && enabled),
                    next => Change(next.ToString()), label: label)
                .AutomationName(label).AutomationId("ProtocolOption-" + field.Key);
        if (field.Choices.Count > 0)
        {
            int index = field.Choices.ToList().FindIndex(choice => string.Equals(choice, value, StringComparison.OrdinalIgnoreCase));
            // Retain unknown stored values visibly so editing never silently replaces bad configuration.
            var choices = index < 0 ? field.Choices.Append(value).ToArray() : field.Choices.ToArray();
            if (index < 0) index = choices.Length - 1;
            return ComboBox(choices.Select(choice => Str.T("protocol.choice." + field.Key + "." + choice,
                    field.ChoiceLabels.TryGetValue(choice, out string? display) ? display : choice)).ToArray(),
                    Optional<int>.Of(index), selected => { if (selected >= 0 && selected < choices.Length) Change(choices[selected]); })
                .Set(control => control.Header = label).HAlign(HorizontalAlignment.Stretch)
                .AutomationName(label).AutomationId("ProtocolOption-" + field.Key);
        }
        return TextBox(Optional<string>.Of(value), Change, header: label)
            .AutomationName(label).AutomationId("ProtocolOption-" + field.Key);
    }
}
