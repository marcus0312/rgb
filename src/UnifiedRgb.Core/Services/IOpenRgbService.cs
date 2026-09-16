using UnifiedRgb.Core.Models;

namespace UnifiedRgb.Core.Services;

public interface IOpenRgbService : IDisposable
{
    bool IsConnected { get; }
    string? LastError { get; }
    string? LastStatus { get; }
    string Host { get; }
    int Port { get; }

    event EventHandler? ConnectionChanged;

    void Connect(string host = "127.0.0.1", int port = 6742, int timeoutMs = 1500);
    void Disconnect();
    IReadOnlyList<DeviceInfo> ListDevices();

    /// <summary>Apply solid Static (legacy). Prefer <see cref="ApplyEffect"/>.</summary>
    void ApplySolidColor(int deviceIndex, RgbColor color, double brightness01 = 1.0);
    void ApplySolidColorToAll(RgbColor color, double brightness01 = 1.0);
    void ApplySolidColorToZone(int deviceIndex, int zoneIndex, RgbColor color, double brightness01 = 1.0);

    /// <summary>
    /// Apply effect mode + color + speed (0–1 UI range mapped to device speed_min/max or CM HID 0–4).
    /// </summary>
    void ApplyEffect(
        int deviceIndex,
        RgbColor color,
        string? modeName,
        double speed01 = 0.5,
        double brightness01 = 1.0);

    void ApplyEffectToAll(
        RgbColor color,
        string? modeName,
        double speed01 = 0.5,
        double brightness01 = 1.0);

    void ApplyEffectToZone(
        int deviceIndex,
        int zoneIndex,
        RgbColor color,
        string? modeName,
        double speed01 = 0.5,
        double brightness01 = 1.0);

    void ResizeZone(int deviceIndex, int zoneIndex, int ledCount);
    void ConfigureZone(int deviceIndex, int zoneIndex, int ledCount);
    bool TrySetHardwareBrightness(int deviceIndex, uint brightness, out string? error);
}
