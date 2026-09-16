using UnifiedRgb.Core.Models;

namespace UnifiedRgb.Core.Services;

public interface IOpenRgbService : IDisposable
{
    bool IsConnected { get; }
    string? LastError { get; }
    string Host { get; }
    int Port { get; }

    event EventHandler? ConnectionChanged;

    void Connect(string host = "127.0.0.1", int port = 6742, int timeoutMs = 1500);
    void Disconnect();
    IReadOnlyList<DeviceInfo> ListDevices();
    void ApplySolidColor(int deviceIndex, RgbColor color, double brightness01 = 1.0);
    void ApplySolidColorToAll(RgbColor color, double brightness01 = 1.0);
    void ApplySolidColorToZone(int deviceIndex, int zoneIndex, RgbColor color, double brightness01 = 1.0);
    void ResizeZone(int deviceIndex, int zoneIndex, int ledCount);
    void ConfigureZone(int deviceIndex, int zoneIndex, int ledCount);
    bool TrySetHardwareBrightness(int deviceIndex, uint brightness, out string? error);
}
