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

    /// <summary>Brief note from the last successful/partial apply (e.g. "CM Gen2: HID Static OK").</summary>
    public string? LastStatus { get; private set; }

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
                var note = ApplyToDevice(deviceIndex, color, brightness01);
                LastError = null;
                LastStatus = note;
            }
            catch (Exception ex)
            {
                LastStatus = null;
                if (ex is InvalidOperationException && OperatingSystem.IsWindows() &&
                    ex.Message.Contains("HID", StringComparison.OrdinalIgnoreCase))
                {
                    LastError = ex.Message;
                    throw;
                }
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

            int count;
            try
            {
                count = _client!.GetControllerCount();
            }
            catch (Exception ex)
            {
                LastStatus = null;
                HandleLostConnection(ex);
                throw;
            }

            var errors = new List<string>();
            var notes = new List<string>();
            var ok = 0;

            for (var i = 0; i < count; i++)
            {
                try
                {
                    var note = ApplyToDevice(i, color, brightness01);
                    ok++;
                    if (!string.IsNullOrWhiteSpace(note))
                        notes.Add(note);
                }
                catch (Exception ex)
                {
                    // Continue per-device so one HID/SDK failure does not abort the rest.
                    string name;
                    try { name = _client!.GetControllerData(i).Name ?? $"Device {i}"; }
                    catch { name = $"Device {i}"; }
                    errors.Add($"{name}: {ex.Message}");
                }
            }

            if (ok == 0 && count > 0)
            {
                LastError = string.Join("; ", errors);
                LastStatus = null;
                throw new InvalidOperationException(
                    "Failed to apply color to all devices. " + LastError);
            }

            LastError = errors.Count > 0 ? string.Join("; ", errors) : null;
            LastStatus = notes.Count > 0
                ? string.Join(" | ", notes)
                : (ok > 0 ? $"Applied to {ok} device(s)" : null);

            if (errors.Count > 0 && ok > 0)
            {
                // Partial success: keep going; surface combined status for UI.
                LastStatus = $"{LastStatus}; partial errors: {LastError}";
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
                var note = ApplyToZone(deviceIndex, zoneIndex, color, brightness01);
                LastError = null;
                LastStatus = note;
            }
            catch (Exception ex)
            {
                LastStatus = null;
                if (ex is InvalidOperationException &&
                    ex.Message.Contains("HID", StringComparison.OrdinalIgnoreCase))
                {
                    LastError = ex.Message;
                    throw;
                }
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
        string? deviceName = null;
        try { deviceName = _client!.GetControllerData(deviceIndex).Name; }
        catch { /* name match is best-effort */ }

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
            },
            preferredDeviceName: deviceName);

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

    private string ApplyToDevice(int deviceIndex, RgbColor color, double brightness01)
    {
        var client = _client!;
        var device = client.GetControllerData(deviceIndex);
        var scaled = color.WithBrightness(brightness01);
        var deviceLabel = device.Name ?? $"Device {deviceIndex}";

        // Cooler Master ARGB Gen2: OpenRGB set_mode/UpdateLeds often leaves Spectrum.
        // Drive solid color via Windows HID Static only — never follow with OpenRGB Direct
        // (SetupDirectMode ResetDevice + black LEDs fights HID Static ~1s later).
        // CM path is HID-only; do not treat ASRock/GALAX/G502 like CM.
        if (TryApplyCmGen2HidStatic(device.Name, scaled))
        {
            // Optional second HID Static for stickiness; still no OpenRGB sync.
            Thread.Sleep(CmArgbGen2HidController.InterPacketDelayMs);
            _ = TryApplyCmGen2HidStatic(device.Name, scaled);
            return $"{deviceLabel}: HID Static x2 (no OpenRGB follow-up)";
        }

        // Non-CM backends (each device has its own stack — not CM HID):
        // - ASRock: no Direct; Static is per-LED (color_mode PER_LED) — UpdateLeds all LEDs
        // - GALAX: Direct + UpdateLeds (typically 1 LED)
        // - G502: Direct + UpdateLeds; quit Logitech G HUB (conflicts)
        // OpenRGB.NET 3.1.1 speaks protocol 4 and sends ordinal indices for UpdateLeds;
        // protocol-6 servers use unique IDs — always apply colors via proto6 unique-ID path.
        var openRgbColor = new Color(scaled.R, scaled.G, scaled.B);
        var modeNote = EnterWritableMode(client, deviceIndex, device, openRgbColor);

        var ledCount = device.Leds.Length;
        if (ledCount == 0)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[UnifiedRgb] Skip UpdateLeds for '{deviceLabel}': LedCount==0 (resize zone or check OpenRGB).");
            return string.IsNullOrEmpty(modeNote)
                ? $"{deviceLabel}: skipped (LedCount==0)"
                : $"{deviceLabel}: {modeNote}; skipped UpdateLeds (LedCount==0)";
        }

        var protoColors = new (byte R, byte G, byte B)[ledCount];
        for (var i = 0; i < ledCount; i++)
            protoColors[i] = (scaled.R, scaled.G, scaled.B);

        OpenRgbProtocol6Client.UpdateLeds(
            Host, Port, Math.Max(_timeoutMs, 2000),
            deviceIndex, device.Name, protoColors);

        // Slow SMBus/USB devices (ASRock, GPU, mouse) often need a beat before the next controller.
        Thread.Sleep(PostUpdateLedsDelayMs);
        var modePart = string.IsNullOrEmpty(modeNote) ? "mode unchanged" : modeNote;
        return $"{deviceLabel}: {modePart} + proto6 UpdateLeds({ledCount})";
    }

    private string ApplyToZone(int deviceIndex, int zoneIndex, RgbColor color, double brightness01)
    {
        var client = _client!;
        var device = client.GetControllerData(deviceIndex);
        if (zoneIndex < 0 || zoneIndex >= device.Zones.Length)
            throw new ArgumentOutOfRangeException(nameof(zoneIndex), $"Zone {zoneIndex} is out of range.");

        var scaled = color.WithBrightness(brightness01);
        var deviceLabel = device.Name ?? $"Device {deviceIndex}";

        // Gen2 Static uses CHANNEL_ALL — solid color applies to the whole hub; HID-only.
        if (TryApplyCmGen2HidStatic(device.Name, scaled))
        {
            Thread.Sleep(CmArgbGen2HidController.InterPacketDelayMs);
            _ = TryApplyCmGen2HidStatic(device.Name, scaled);
            return $"{deviceLabel}: HID Static x2 (no OpenRGB follow-up)";
        }

        // Non-CM: mode enter best-effort via OpenRGB.NET, colors always via proto6 unique IDs.
        var openRgbColor = new Color(scaled.R, scaled.G, scaled.B);
        var modeNote = EnterWritableMode(client, deviceIndex, device, openRgbColor);

        var zone = device.Zones[zoneIndex];
        var ledCount = (int)zone.LedCount;
        if (ledCount == 0)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[UnifiedRgb] Skip UpdateZoneLeds for '{deviceLabel}' zone {zoneIndex}: LedCount==0.");
            return string.IsNullOrEmpty(modeNote)
                ? $"{deviceLabel} zone {zoneIndex}: skipped (LedCount==0)"
                : $"{deviceLabel} zone {zoneIndex}: {modeNote}; skipped UpdateZoneLeds (LedCount==0)";
        }

        var protoColors = new (byte R, byte G, byte B)[ledCount];
        for (var i = 0; i < ledCount; i++)
            protoColors[i] = (scaled.R, scaled.G, scaled.B);

        OpenRgbProtocol6Client.UpdateZoneLeds(
            Host, Port, Math.Max(_timeoutMs, 2000),
            deviceIndex, device.Name, zoneIndex, protoColors);

        Thread.Sleep(PostUpdateLedsDelayMs);
        var modePart = string.IsNullOrEmpty(modeNote) ? "mode unchanged" : modeNote;
        return $"{deviceLabel} zone {zoneIndex}: {modePart} + proto6 UpdateZoneLeds({ledCount})";
    }

    /// <summary>
    /// When the OpenRGB device name is Cooler Master ARGB Gen2, send HID Static.
    /// Throws if matching device but HID write fails on Windows (so UI shows the error).
    /// Non-Windows / non-CM devices return false and keep the OpenRGB.NET path.
    /// Intentionally does NOT call OpenRGB SetCustomMode/UpdateMode/UpdateLeds afterward —
    /// CM Gen2 SetupDirectMode resets the hub and blacks LEDs, undoing HID Static.
    /// </summary>
    private static bool TryApplyCmGen2HidStatic(string? deviceName, RgbColor scaled)
    {
        if (!CmArgbGen2HidController.IsCoolerMasterArgbGen2(deviceName))
            return false;

        if (CmArgbGen2HidController.TrySetStaticColor(scaled.R, scaled.G, scaled.B, out var hidError))
            return true;

        if (!OperatingSystem.IsWindows())
            return false;

        throw new InvalidOperationException(
            hidError ?? "Cooler Master ARGB Gen2 HID Static failed.");
    }

    private const int PostUpdateLedsDelayMs = 50;

    /// <summary>
    /// Enter a writable lighting mode for Apply-to-selected (one device at a time).
    /// Best-effort via OpenRGB.NET (protocol 4 ordinals); colors are applied afterward
    /// via protocol-6 unique-ID UpdateLeds. Order: Direct (GALAX/G502) → Custom →
    /// SetCustomMode → Static (ASRock — no Direct; Static is often PER_LED).
    /// Returns a short note for LastStatus.
    /// </summary>
    private static string EnterWritableMode(
        OpenRgbClient client, int deviceIndex, Device device, Color solidColor)
    {
        // Named Direct first so GPU/mouse get per-LED Direct, not a vague SetCustomMode.
        if (TryUpdateNamedMode(client, deviceIndex, device, solidColor, "Direct", out var note))
            return note;
        if (TryUpdateNamedMode(client, deviceIndex, device, solidColor, "Custom", out note))
            return note;

        try
        {
            client.SetCustomMode(deviceIndex);
            return "SetCustomMode";
        }
        catch
        {
            // Static-only boards (e.g. some ASRock Polychrome) reject SetCustomMode.
        }

        if (TryUpdateNamedMode(client, deviceIndex, device, solidColor, "Static", out note))
            return note;

        return "";
    }

    private static bool TryUpdateNamedMode(
        OpenRgbClient client,
        int deviceIndex,
        Device device,
        Color solidColor,
        string needle,
        out string note)
    {
        note = "";
        for (var i = 0; i < device.Modes.Length; i++)
        {
            var mode = device.Modes[i];
            var name = mode.Name ?? "";
            if (!name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                if (ModeNeedsModeSpecificColors(mode))
                {
                    var modeColors = BuildModeSpecificColors(mode, solidColor);
                    client.UpdateMode(deviceIndex, i, colors: modeColors);
                    note = $"UpdateMode({name}+colors)";
                    return true;
                }

                client.UpdateMode(deviceIndex, i);
                note = $"UpdateMode({name})";
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[UnifiedRgb] UpdateMode({name}) failed on device {deviceIndex}: {ex.Message}");
            }
        }

        return false;
    }

    private static bool ModeNeedsModeSpecificColors(Mode mode)
    {
        if (mode.Flags.HasFlag(ModeFlags.HasModeSpecificColor))
            return true;
        return mode.ColorMode == ColorMode.ModeSpecific;
    }

    private static Color[] BuildModeSpecificColors(Mode mode, Color solid)
    {
        var existing = mode.Colors;
        var count = existing is { Length: > 0 }
            ? existing.Length
            : (int)Math.Max(1, mode.ColorMin == 0 && mode.ColorMax == 0 ? 1 : mode.ColorMin);
        if (mode.ColorMax > 0)
            count = Math.Min(count, (int)mode.ColorMax);
        if (mode.ColorMin > 0)
            count = Math.Max(count, (int)mode.ColorMin);
        count = Math.Max(1, count);

        var colors = new Color[count];
        Array.Fill(colors, solid);
        return colors;
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
