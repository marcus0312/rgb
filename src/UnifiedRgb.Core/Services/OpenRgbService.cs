using OpenRGB.NET;
using UnifiedRgb.Core.Models;

namespace UnifiedRgb.Core.Services;

/// <summary>
/// Thin wrapper around OpenRGB.NET's <see cref="OpenRgbClient"/>.
/// Talks only to a local OpenRGB SDK server (default 127.0.0.1:6742).
/// </summary>
public sealed class OpenRgbService : IOpenRgbService
{
    private OpenRgbClient? _client;
    private readonly object _gate = new();

    public bool IsConnected
    {
        get
        {
            lock (_gate) return _client?.Connected == true;
        }
    }

    public string? LastError { get; private set; }
    public string Host { get; private set; } = "127.0.0.1";
    public int Port { get; private set; } = 6742;

    public event EventHandler? ConnectionChanged;

    public void Connect(string host = "127.0.0.1", int port = 6742, int timeoutMs = 1500)
    {
        lock (_gate)
        {
            DisconnectInternal();
            Host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
            Port = port <= 0 ? 6742 : port;

            try
            {
                _client = new OpenRgbClient(
                    ip: Host,
                    port: Port,
                    name: "UnifiedRgb",
                    autoConnect: false,
                    timeoutMs: timeoutMs);

                _client.Connect();

                if (!_client.Connected)
                    throw new InvalidOperationException("OpenRGB SDK did not report Connected after Connect().");

                LastError = null;
            }
            catch (Exception ex)
            {
                DisposeClient();
                LastError = FormatConnectError(ex);
                RaiseChanged();
                throw new InvalidOperationException(LastError, ex);
            }
        }

        RaiseChanged();
    }

    public void Disconnect()
    {
        lock (_gate)
        {
            DisconnectInternal();
            LastError = null;
        }

        RaiseChanged();
    }

    public IReadOnlyList<DeviceInfo> ListDevices()
    {
        lock (_gate)
        {
            EnsureConnected();
            try
            {
                var devices = _client!.GetAllControllerData();
                LastError = null;
                var list = new List<DeviceInfo>(devices.Length);
                for (var i = 0; i < devices.Length; i++)
                    list.Add(MapDevice(devices[i], i));
                return list;
            }
            catch (Exception ex)
            {
                HandleLostConnection(ex);
                throw;
            }
        }
    }

    public void ApplySolidColor(int deviceIndex, RgbColor color, double brightness01 = 1.0)
    {
        lock (_gate)
        {
            EnsureConnected();
            try
            {
                ApplyToDevice(_client!, deviceIndex, color, brightness01);
                LastError = null;
            }
            catch (Exception ex)
            {
                HandleLostConnection(ex);
                throw;
            }
        }
    }

    public void ApplySolidColorToAll(RgbColor color, double brightness01 = 1.0)
    {
        lock (_gate)
        {
            EnsureConnected();
            try
            {
                var count = _client!.GetControllerCount();
                for (var i = 0; i < count; i++)
                    ApplyToDevice(_client, i, color, brightness01);

                LastError = null;
            }
            catch (Exception ex)
            {
                HandleLostConnection(ex);
                throw;
            }
        }
    }

    /// <summary>
    /// Hardware brightness via OpenRGB mode flags.
    /// OpenRGB.NET 3.1.1 exposes Mode.SupportsBrightness / SetBrightness, but UpdateMode
    /// re-fetches the device and only applies speed/direction/colors parameters — so hardware
    /// brightness cannot be set through the public API. Returns false and skips cleanly.
    /// </summary>
    public bool TrySetHardwareBrightness(int deviceIndex, uint brightness, out string? error)
    {
        lock (_gate)
        {
            EnsureConnected();
            try
            {
                var device = _client!.GetControllerData(deviceIndex);
                var mode = device.ActiveMode;
                if (mode is null || !mode.SupportsBrightness)
                {
                    error = "Active mode does not support hardware brightness.";
                    return false;
                }

                // Quirk: no brightness arg on UpdateMode; mutation would be discarded on re-fetch.
                _ = brightness;
                error = "OpenRGB.NET UpdateMode does not accept brightness; client uses RGB scaling instead.";
                return false;
            }
            catch (Exception ex)
            {
                HandleLostConnection(ex);
                error = ex.Message;
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            DisconnectInternal();
        }
    }

    private static void ApplyToDevice(OpenRgbClient client, int deviceIndex, RgbColor color, double brightness01)
    {
        var device = client.GetControllerData(deviceIndex);
        var ledCount = device.Leds.Length;
        if (ledCount == 0)
            return;

        TryEnterDirectOrCustom(client, deviceIndex, device);

        var scaled = color.WithBrightness(brightness01);
        var openRgbColor = new Color(scaled.R, scaled.G, scaled.B);
        var colors = new Color[ledCount];
        Array.Fill(colors, openRgbColor);
        client.UpdateLeds(deviceIndex, colors);
    }

    private static void TryEnterDirectOrCustom(OpenRgbClient client, int deviceIndex, Device device)
    {
        try
        {
            client.SetCustomMode(deviceIndex);
            return;
        }
        catch
        {
            // Some devices reject SetCustomMode
        }

        try
        {
            for (var i = 0; i < device.Modes.Length; i++)
            {
                var name = device.Modes[i].Name ?? "";
                if (name.Contains("Direct", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Custom", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Static", StringComparison.OrdinalIgnoreCase))
                {
                    client.UpdateMode(deviceIndex, i);
                    return;
                }
            }
        }
        catch
        {
            // Best-effort
        }
    }

    private static DeviceInfo MapDevice(Device d, int index)
    {
        RgbColor? current = null;
        if (d.Colors is { Length: > 0 })
        {
            var c = d.Colors[0];
            current = new RgbColor(c.R, c.G, c.B);
        }

        var mode = d.ActiveMode;
        return new DeviceInfo
        {
            Index = index,
            Name = d.Name ?? $"Device {index}",
            Vendor = d.Vendor ?? "",
            Type = d.Type.ToString(),
            Description = d.Description ?? "",
            LedCount = d.Leds.Length,
            ActiveModeName = mode?.Name,
            ActiveModeSupportsBrightness = mode?.SupportsBrightness == true,
            Brightness = mode?.SupportsBrightness == true ? mode.Brightness : null,
            BrightnessMin = mode?.SupportsBrightness == true ? mode.BrightnessMin : null,
            BrightnessMax = mode?.SupportsBrightness == true ? mode.BrightnessMax : null,
            CurrentColor = current,
            Zones = d.Zones.Select(z => new ZoneInfo
            {
                Index = z.Index,
                Name = z.Name ?? $"Zone {z.Index}",
                LedCount = z.LedCount,
                Type = z.Type.ToString()
            }).ToList()
        };
    }

    private void EnsureConnected()
    {
        if (_client?.Connected != true)
        {
            LastError = $"Not connected to OpenRGB SDK at {Host}:{Port}. Start OpenRGB with SDK Server enabled.";
            throw new InvalidOperationException(LastError);
        }
    }

    private void HandleLostConnection(Exception ex)
    {
        LastError = $"Connection lost or OpenRGB error: {ex.Message}";
        DisposeClient();
        RaiseChanged();
    }

    private void DisconnectInternal() => DisposeClient();

    private void DisposeClient()
    {
        try { _client?.Dispose(); }
        catch { /* ignore */ }
        _client = null;
    }

    private void RaiseChanged() => ConnectionChanged?.Invoke(this, EventArgs.Empty);

    private string FormatConnectError(Exception ex)
    {
        var msg = ex.InnerException?.Message ?? ex.Message;
        return $"Cannot connect to OpenRGB SDK at {Host}:{Port}. " +
               "Is OpenRGB running with SDK Server started? " +
               $"Details: {msg}";
    }
}
