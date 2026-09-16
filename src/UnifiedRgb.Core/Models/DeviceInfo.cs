namespace UnifiedRgb.Core.Models;

public sealed class ZoneInfo
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public uint LedCount { get; init; }
    public string Type { get; init; } = "";
}

public sealed class DeviceInfo
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string Vendor { get; init; } = "";
    public string Type { get; init; } = "";
    public string Description { get; init; } = "";
    public int LedCount { get; init; }
    public string? ActiveModeName { get; init; }
    public bool ActiveModeSupportsBrightness { get; init; }
    public uint? Brightness { get; init; }
    public uint? BrightnessMin { get; init; }
    public uint? BrightnessMax { get; init; }
    public IReadOnlyList<ZoneInfo> Zones { get; init; } = Array.Empty<ZoneInfo>();
    public RgbColor? CurrentColor { get; init; }

    public string ZonesSummary =>
        Zones.Count == 0
            ? "—"
            : string.Join(", ", Zones.Select(z => $"{z.Name} ({z.LedCount})"));

    public string DisplayLine =>
        $"{Name}  ·  {LedCount} LEDs  ·  {Zones.Count} zone(s)" +
        (string.IsNullOrWhiteSpace(ActiveModeName) ? "" : $"  ·  mode: {ActiveModeName}");
}
