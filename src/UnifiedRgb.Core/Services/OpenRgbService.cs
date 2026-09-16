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
    private int _timeoutMs = 1500;

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
            _timeoutMs = timeoutMs <= 0 ? 1500 : timeoutMs;

            try
            {
                _client = new OpenRgbClient(
                    ip: Host,
                    port: Port,
                    name: "UnifiedRgb",
                    autoConnect: false,
                    timeoutMs: _timeoutMs);

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

    public void ApplySolidColorToZone(int deviceIndex, int zoneIndex, RgbColor color, double brightness01 = 1.0)
    {
        lock (_gate)
        {
            EnsureConnected();
            try
            {
                ApplyToZone(_client!, deviceIndex, zoneIndex, color, brightness01);
                LastError = null;
            }
            catch (Exception ex)
            {
                HandleLostConnection(ex);
                throw;
            }
        }
    }

    public void ResizeZone(int deviceIndex, int zoneIndex, int ledCount)
    {
        lock (_gate)
        {
            EnsureConnected();
            Zone zone;
            try
            {
                zone = ValidateZoneSize(deviceIndex, zoneIndex, ledCount);

                // OpenRGB.NET ResizeZone maps to packet 1000. That is a no-op when the
                // driver did not set ZONE_FLAG_MANUALLY_CONFIGURABLE_SIZE (CM ARGB Gen2).
                _client!.ResizeZone(deviceIndex, zoneIndex, ledCount);

                var after = _client.GetControllerData(deviceIndex).Zones[zoneIndex];
                if (after.LedCount == (uint)ledCount)
                {
                    LastError = null;
                    return;
                }
            }
            catch (Exception ex) when (ex is not ArgumentOutOfRangeException)
            {
                HandleLostConnection(ex);
                throw;
            }

            // Fallback: protocol 6 ConfigureZone (packet 1003) on a dedicated TCP
            // connection. OpenRGB.NET 3.1.1 max protocol is 4 and has no ConfigureZone;
            // injecting into its socket would parse Zone Data without flags.
            try
            {
                ConfigureZoneLocked(deviceIndex, zoneIndex, ledCount, zone);
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                throw new InvalidOperationException(ex.Message, ex);
            }
        }
    }

    public void ConfigureZone(int deviceIndex, int zoneIndex, int ledCount)
    {
        lock (_gate)
        {
            EnsureConnected();
            Zone zone;
            try
            {
                zone = ValidateZoneSize(deviceIndex, zoneIndex, ledCount);
            }
            catch (Exception ex) when (ex is not ArgumentOutOfRangeException)
            {
                HandleLostConnection(ex);
                throw;
            }

            try
            {
                ConfigureZoneLocked(deviceIndex, zoneIndex, ledCount, zone);
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                throw new InvalidOperationException(ex.Message, ex);
            }
        }
    }

    private Zone ValidateZoneSize(int deviceIndex, int zoneIndex, int ledCount)
    {
        if (ledCount < 0)
            throw new ArgumentOutOfRangeException(nameof(ledCount), "LED count must be >= 0.");

        var device = _client!.GetControllerData(deviceIndex);
        if (zoneIndex < 0 || zoneIndex >= device.Zones.Length)
            throw new ArgumentOutOfRangeException(nameof(zoneIndex), $"Zone {zoneIndex} is out of range.");

        var zone = device.Zones[zoneIndex];
        if (zone.LedsMax > 0 && (uint)ledCount > zone.LedsMax)
            throw new ArgumentOutOfRangeException(nameof(ledCount),
                $"LED count {ledCount} exceeds zone max {zone.LedsMax}.");
        if (zone.LedsMin > 0 && (uint)ledCount < zone.LedsMin && ledCount != 0)
            throw new ArgumentOutOfRangeException(nameof(ledCount),
                $"LED count {ledCount} is below zone min {zone.LedsMin}.");
        return zone;
    }

    private void ConfigureZoneLocked(int deviceIndex, int zoneIndex, int ledCount, Zone zone)
    {
        OpenRgbConfigureZoneClient.ConfigureZone(
            Host,
            Port,
            Math.Max(_timeoutMs, 2000),
            deviceIndex,
            zoneIndex,
            new OpenRgbZoneConfig
            {
                Name = zone.Name ?? $"Zone {zoneIndex}",
                Type = MapZoneType(zone.Type),
                LedsMin = zone.LedsMin,
                LedsMax = zone.LedsMax > 0 ? zone.LedsMax : 72,
                LedsCount = (uint)ledCount,
                Flags = OpenRgbConfigureZoneClient.ZoneFlagManuallyConfigurableSize
                      | OpenRgbConfigureZoneClient.ZoneFlagManuallyConfiguredSize
            });

        var verify = _client!.GetControllerData(deviceIndex);
        if (zoneIndex >= verify.Zones.Length)
            throw new InvalidOperationException("Zone disappeared after ConfigureZone.");

        var after = verify.Zones[zoneIndex];
        if (after.LedCount != (uint)ledCount)
        {
            throw new InvalidOperationException(
                $"ConfigureZone did not change LED count (still {after.LedCount}, wanted {ledCount}). " +
                "OpenRGB 1.0+ SDK is required; OpenRGB GUI also hides Edit Zone for CM Gen2.");
        }
    }

    private static int MapZoneType(ZoneType type) =>
        type switch
        {
            ZoneType.Single => 0,
            ZoneType.Linear => OpenRgbConfigureZoneClient.ZoneTypeLinear,
            ZoneType.Matrix => 2,
            _ => OpenRgbConfigureZoneClient.ZoneTypeLinear
        };

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

    private static void ApplyToZone(OpenRgbClient client, int deviceIndex, int zoneIndex, RgbColor color, double brightness01)
    {
        var device = client.GetControllerData(deviceIndex);
        if (zoneIndex < 0 || zoneIndex >= device.Zones.Length)
            throw new ArgumentOutOfRangeException(nameof(zoneIndex), $"Zone {zoneIndex} is out of range.");

        var zone = device.Zones[zoneIndex];
        var ledCount = (int)zone.LedCount;
        if (ledCount == 0)
            return;

        TryEnterDirectOrCustom(client, deviceIndex, device);

        var scaled = color.WithBrightness(brightness01);
        var openRgbColor = new Color(scaled.R, scaled.G, scaled.B);
        var colors = new Color[ledCount];
        Array.Fill(colors, openRgbColor);
        client.UpdateZoneLeds(deviceIndex, zoneIndex, colors);
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
                LedsMin = z.LedsMin,
                LedsMax = z.LedsMax,
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
