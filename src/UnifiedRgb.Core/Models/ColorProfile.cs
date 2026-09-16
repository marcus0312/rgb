namespace UnifiedRgb.Core.Models;

public sealed class DeviceColorEntry
{
    public string DeviceName { get; set; } = "";
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
}

public sealed class ColorProfile
{
    public string Name { get; set; } = "";
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;
    public double Brightness { get; set; } = 1.0;
    public List<DeviceColorEntry> Devices { get; set; } = new();
}
