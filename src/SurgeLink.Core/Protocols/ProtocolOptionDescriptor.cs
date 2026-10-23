using System.Globalization;

namespace SurgeLink.Core.Protocols;

/// <summary>A persisted protocol setting. Choice values are stable; labels may be localized.</summary>
public sealed record ProtocolOptionDescriptor(string Key, string Label, string DefaultValue)
{
    public IReadOnlyList<string> Choices { get; init; } = [];
    public IReadOnlyDictionary<string, string> ChoiceLabels { get; init; } = new Dictionary<string, string>();
    public bool IsBoolean { get; init; }
    public int? Minimum { get; init; }
    public int? Maximum { get; init; }

    public bool IsValid(string value)
    {
        if (IsBoolean) return bool.TryParse(value, out _);
        if (Choices.Count > 0) return Choices.Contains(value, StringComparer.OrdinalIgnoreCase);
        if (Minimum is not null || Maximum is not null)
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) &&
                (Minimum is null || number >= Minimum) && (Maximum is null || number <= Maximum);
        return true;
    }
}
