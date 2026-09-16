namespace UnifiedRgb.Core.Models;

/// <summary>OpenRGB (or curated) lighting mode descriptor for the UI and apply path.</summary>
public sealed class ModeInfo
{
    public int Index { get; init; } = -1;
    public string Name { get; init; } = "";
    public bool SupportsSpeed { get; init; }
    public uint SpeedMin { get; init; }
    public uint SpeedMax { get; init; }
    public bool SupportsBrightness { get; init; }
    public bool HasPerLedColor { get; init; }
    public bool HasModeSpecificColor { get; init; }
    public string ColorMode { get; init; } = "";

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name) ? $"Mode {Index}" : Name;
}

/// <summary>Curated effect names always offered in the UI, plus helpers.</summary>
public static class EffectModes
{
    public static readonly string[] Curated =
    {
        "Static",
        "Direct",
        "Breathing",
        "Spectrum",
        "Rainbow",
        "Off",
        "Demo",
        "Reload",
        "Recoil",
        "Refill",
        "Fill Flow",
        "Custom"
    };

    /// <summary>Merge curated names with device-reported modes (device names win order after curated).</summary>
    public static IReadOnlyList<string> BuildPickerList(IEnumerable<ModeInfo>? deviceModes)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var name in Curated)
        {
            if (seen.Add(name))
                list.Add(name);
        }

        if (deviceModes is null)
            return list;

        foreach (var mode in deviceModes)
        {
            if (string.IsNullOrWhiteSpace(mode.Name))
                continue;
            if (seen.Add(mode.Name))
                list.Add(mode.Name);
        }

        return list;
    }

    public static bool NamesMatch(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) &&
        !string.IsNullOrWhiteSpace(b) &&
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    public static bool NameContains(string? haystack, string needle) =>
        !string.IsNullOrWhiteSpace(haystack) &&
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
