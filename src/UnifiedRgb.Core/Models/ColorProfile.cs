namespace UnifiedRgb.Core.Models;

/// <summary>
/// Per-device snapshot in a profile. Older profiles may omit ModeName/Speed/Brightness;
/// missing fields default so load stays backward compatible.
/// </summary>
public sealed class DeviceColorEntry
{
    public string DeviceName { get; set; } = "";
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }

    /// <summary>Effect mode name (e.g. Static, Breathing). Null/empty → Static on apply.</summary>
    public string? ModeName { get; set; }

    /// <summary>Effect speed 0–100. Null → use UI/default mid speed.</summary>
    public double? Speed { get; set; }

    /// <summary>
    /// Optional per-device brightness 0–1. Null → use profile-level <see cref="ColorProfile.Brightness"/>.
    /// </summary>
    public double? Brightness { get; set; }
}

public sealed class ColorProfile
{
    public string Name { get; set; } = "";
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;
    public double Brightness { get; set; } = 1.0;

    /// <summary>Optional profile-wide default mode (used when a device entry has no ModeName).</summary>
    public string? ModeName { get; set; }

    /// <summary>Optional profile-wide default speed 0–100.</summary>
    public double? Speed { get; set; }

    public List<DeviceColorEntry> Devices { get; set; } = new();
}

/// <summary>In-memory per-device effect state for the UI (independent of OpenRGB refresh).</summary>
public sealed class DeviceEffectState
{
    public byte R { get; set; } = 255;
    public byte G { get; set; }
    public byte B { get; set; }
    public string ModeName { get; set; } = "Static";
    public double Speed { get; set; } = 50;
    public double Brightness { get; set; } = 100;
}
