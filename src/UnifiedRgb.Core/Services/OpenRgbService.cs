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
    /// <summary>When true (Sync all), skip inter-device sleeps so hardware effects start in phase.</summary>
    private bool _syncBatch;

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

    public void ApplySolidColor(int deviceIndex, RgbColor color, double brightness01 = 1.0) =>
        ApplyEffect(deviceIndex, color, "Static", speed01: 0.5, brightness01);

    public void ApplySolidColorToAll(RgbColor color, double brightness01 = 1.0) =>
        ApplyEffectToAll(color, "Static", speed01: 0.5, brightness01);

    public void ApplySolidColorToZone(int deviceIndex, int zoneIndex, RgbColor color, double brightness01 = 1.0) =>
        ApplyEffectToZone(deviceIndex, zoneIndex, color, "Static", speed01: 0.5, brightness01);

    public void ApplyEffect(
        int deviceIndex,
        RgbColor color,
        string? modeName,
        double speed01 = 0.5,
        double brightness01 = 1.0)
    {
        lock (_gate)
        {
            EnsureConnected();
            try
            {
                var note = ApplyEffectToDevice(deviceIndex, color, modeName, speed01, brightness01);
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

    public void ApplyEffectToAll(
        RgbColor color,
        string? modeName,
        double speed01 = 0.5,
        double brightness01 = 1.0)
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
            var modeLabel = string.IsNullOrWhiteSpace(modeName) ? "Static" : modeName.Trim();
            var tier = EffectSpeedSync.ToTier(speed01);

            // Sync all: minimal/no inter-device delay so Breathing (etc.) starts in phase.
            _syncBatch = true;
            try
            {
                for (var i = 0; i < count; i++)
                {
                    try
                    {
                        var note = ApplyEffectToDevice(i, color, modeLabel, speed01, brightness01);
                        ok++;
                        if (!string.IsNullOrWhiteSpace(note))
                            notes.Add(note);
                    }
                    catch (Exception ex)
                    {
                        string name;
                        try { name = _client!.GetControllerData(i).Name ?? $"Device {i}"; }
                        catch { name = $"Device {i}"; }
                        errors.Add($"{name}: {ex.Message}");
                    }
                }

                // Second pass: re-assert the same mode+speed so devices that finished
                // first are nudged back into the same tier after the batch completes.
                if (ok > 0 && NeedsSpeedReassert(modeLabel))
                {
                    for (var i = 0; i < count; i++)
                    {
                        try
                        {
                            _ = ApplyEffectToDevice(i, color, modeLabel, speed01, brightness01);
                        }
                        catch
                        {
                            // Best-effort re-assert; first-pass notes/errors already recorded.
                        }
                    }
                }
            }
            finally
            {
                _syncBatch = false;
            }

            if (ok == 0 && count > 0)
            {
                LastError = string.Join("; ", errors);
                LastStatus = null;
                throw new InvalidOperationException(
                    "Failed to apply effect to all devices. " + LastError);
            }

            LastError = errors.Count > 0 ? string.Join("; ", errors) : null;
            var header = $"sync tier {tier}/{EffectSpeedSync.TierCount - 1}";
            LastStatus = notes.Count > 0
                ? $"{header}: " + string.Join(" | ", notes)
                : (ok > 0 ? $"{header}: applied to {ok} device(s)" : null);

            if (errors.Count > 0 && ok > 0)
                LastStatus = $"{LastStatus}; partial errors: {LastError}";
        }
    }

    public void ApplyEffectToZone(
        int deviceIndex,
        int zoneIndex,
        RgbColor color,
        string? modeName,
        double speed01 = 0.5,
        double brightness01 = 1.0)
    {
        lock (_gate)
        {
            EnsureConnected();
            try
            {
                var note = ApplyEffectToZoneLocked(deviceIndex, zoneIndex, color, modeName, speed01, brightness01);
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

    private string ApplyEffectToDevice(
        int deviceIndex,
        RgbColor color,
        string? modeName,
        double speed01,
        double brightness01)
    {
        var client = _client!;
        var device = client.GetControllerData(deviceIndex);
        var scaled = color.WithBrightness(brightness01);
        var deviceLabel = device.Name ?? $"Device {deviceIndex}";
        var modeLabel = string.IsNullOrWhiteSpace(modeName) ? "Static" : modeName.Trim();
        speed01 = Math.Clamp(speed01, 0, 1);
        brightness01 = Math.Clamp(brightness01, 0, 1);

        // Cooler Master ARGB Gen2: HID hardware modes only — never OpenRGB Direct follow-up.
        if (TryApplyCmGen2HidEffect(device.Name, modeLabel, scaled, speed01, brightness01, out var cmNote))
        {
            // Optional second HID write for Static stickiness; still no OpenRGB sync.
            if (CmArgbGen2HidController.TryMapModeName(modeLabel) is byte m &&
                m == CmArgbGen2HidController.HwModeStatic)
            {
                Thread.Sleep(CmArgbGen2HidController.InterPacketDelayMs);
                _ = TryApplyCmGen2HidEffect(device.Name, modeLabel, scaled, speed01, brightness01, out _);
                return $"{deviceLabel}: {cmNote} x2 (no OpenRGB follow-up)";
            }

            return $"{deviceLabel}: {cmNote} (no OpenRGB follow-up)";
        }

        return ApplyOpenRgbEffect(deviceIndex, device, scaled, modeLabel, speed01, brightness01, zoneIndex: null);
    }

    private string ApplyEffectToZoneLocked(
        int deviceIndex,
        int zoneIndex,
        RgbColor color,
        string? modeName,
        double speed01,
        double brightness01)
    {
        var client = _client!;
        var device = client.GetControllerData(deviceIndex);
        if (zoneIndex < 0 || zoneIndex >= device.Zones.Length)
            throw new ArgumentOutOfRangeException(nameof(zoneIndex), $"Zone {zoneIndex} is out of range.");

        var scaled = color.WithBrightness(brightness01);
        var deviceLabel = device.Name ?? $"Device {deviceIndex}";
        var modeLabel = string.IsNullOrWhiteSpace(modeName) ? "Static" : modeName.Trim();
        speed01 = Math.Clamp(speed01, 0, 1);
        brightness01 = Math.Clamp(brightness01, 0, 1);

        // Gen2 HW modes use CHANNEL_ALL — applies to the whole hub; HID-only.
        if (TryApplyCmGen2HidEffect(device.Name, modeLabel, scaled, speed01, brightness01, out var cmNote))
        {
            if (CmArgbGen2HidController.TryMapModeName(modeLabel) is byte m &&
                m == CmArgbGen2HidController.HwModeStatic)
            {
                Thread.Sleep(CmArgbGen2HidController.InterPacketDelayMs);
                _ = TryApplyCmGen2HidEffect(device.Name, modeLabel, scaled, speed01, brightness01, out _);
                return $"{deviceLabel}: {cmNote} x2 (no OpenRGB follow-up)";
            }

            return $"{deviceLabel}: {cmNote} (no OpenRGB follow-up)";
        }

        return ApplyOpenRgbEffect(deviceIndex, device, scaled, modeLabel, speed01, brightness01, zoneIndex);
    }

    /// <summary>
    /// Non-CM: protocol-6 UpdateMode (unique IDs) with speed + mode colors when needed,
    /// then UpdateLeds / UpdateZoneLeds for Direct or per-LED Static.
    /// </summary>
    private string ApplyOpenRgbEffect(
        int deviceIndex,
        Device device,
        RgbColor scaled,
        string modeName,
        double speed01,
        double brightness01,
        int? zoneIndex)
    {
        var deviceLabel = device.Name ?? $"Device {deviceIndex}";
        var openRgbColor = new Color(scaled.R, scaled.G, scaled.B);

        var modeMatch = FindMode(device, modeName);
        string modeNote;
        Mode? appliedMode = null;
        var wantsPerLedFollowUp = false;

        if (modeMatch is { } found)
        {
            appliedMode = found.Mode;
            var speed = MapSpeed01(found.Mode, speed01);
            var appliedName = found.Mode.Name ?? $"Mode {found.Index}";
            modeNote = UpdateModeProtocol6(deviceIndex, device, found.Index, found.Mode, speed, openRgbColor);
            if (!EffectModes.NamesMatch(appliedName, modeName) &&
                !(appliedName.Contains(modeName, StringComparison.OrdinalIgnoreCase) ||
                  modeName.Contains(appliedName, StringComparison.OrdinalIgnoreCase)))
            {
                modeNote = $"{modeNote} [closest for '{modeName}' → '{appliedName}']";
            }
            wantsPerLedFollowUp = ModeWantsPerLedFollowUp(found.Mode);
        }
        else
        {
            // Fall back to writable Direct/Custom/Static enter (legacy solid path).
            modeNote = EnterWritableMode(deviceIndex, device, openRgbColor, speed01);
            if (string.IsNullOrEmpty(modeNote))
                modeNote = $"no mode match for '{modeName}'; Direct/Static fallback";
            else
                modeNote = $"{modeNote} [fallback for '{modeName}']";
            appliedMode = device.ActiveMode;
            wantsPerLedFollowUp = true;
            // Refresh after mode enter so LED counts stay accurate.
            try { device = _client!.GetControllerData(deviceIndex); } catch { /* keep */ }
        }

        if (!wantsPerLedFollowUp)
        {
            MaybeInterDeviceDelay();
            return $"{deviceLabel}: {modeNote} (hardware effect, no UpdateLeds)";
        }

        if (zoneIndex is int zi)
        {
            var zone = device.Zones[zi];
            var ledCount = (int)zone.LedCount;
            if (ledCount == 0)
            {
                return string.IsNullOrEmpty(modeNote)
                    ? $"{deviceLabel} zone {zi}: skipped (LedCount==0)"
                    : $"{deviceLabel} zone {zi}: {modeNote}; skipped UpdateZoneLeds (LedCount==0)";
            }

            var protoColors = new (byte R, byte G, byte B)[ledCount];
            for (var i = 0; i < ledCount; i++)
                protoColors[i] = (scaled.R, scaled.G, scaled.B);

            OpenRgbProtocol6Client.UpdateZoneLeds(
                Host, Port, Math.Max(_timeoutMs, 2000),
                deviceIndex, device.Name, zi, protoColors);

            MaybeInterDeviceDelay();
            var modePart = string.IsNullOrEmpty(modeNote) ? "mode unchanged" : modeNote;
            return $"{deviceLabel} zone {zi}: {modePart} + proto6 UpdateZoneLeds({ledCount})";
        }
        else
        {
            var ledCount = device.Leds.Length;
            if (ledCount == 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[UnifiedRgb] Skip UpdateLeds for '{deviceLabel}': LedCount==0.");
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

            MaybeInterDeviceDelay();
            var modePart = string.IsNullOrEmpty(modeNote) ? "mode unchanged" : modeNote;
            return $"{deviceLabel}: {modePart} + proto6 UpdateLeds({ledCount})";
        }
    }

    private static bool ModeWantsPerLedFollowUp(Mode mode)
    {
        var name = mode.Name ?? "";
        if (name.Contains("Direct", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Custom", StringComparison.OrdinalIgnoreCase))
            return true;

        if (mode.Flags.HasFlag(ModeFlags.HasPerLedColor) || mode.ColorMode == ColorMode.PerLed)
            return true;

        // Static with per-LED colors (ASRock) needs UpdateLeds; mode-specific-only effects do not.
        if (name.Contains("Static", StringComparison.OrdinalIgnoreCase) &&
            (mode.Flags.HasFlag(ModeFlags.HasPerLedColor) || mode.ColorMode == ColorMode.PerLed))
            return true;

        if (mode.ColorMode == ColorMode.ModeSpecific || mode.Flags.HasFlag(ModeFlags.HasModeSpecificColor))
            return false;

        // Unknown hardware effect: trust UpdateMode alone.
        if (!name.Contains("Static", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("Direct", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static uint MapSpeed01(Mode mode, double speed01) =>
        EffectSpeedSync.MapOpenRgb(
            mode.SpeedMin,
            mode.SpeedMax,
            mode.SupportsSpeed,
            mode.Speed,
            speed01);

    /// <summary>Hardware effects with speed benefit from a Sync-all re-assert pass.</summary>
    private static bool NeedsSpeedReassert(string modeName)
    {
        if (string.IsNullOrWhiteSpace(modeName))
            return false;
        var n = modeName.Trim();
        if (n.Contains("Static", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Direct", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Custom", StringComparison.OrdinalIgnoreCase) ||
            n.Equals("Off", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private void MaybeInterDeviceDelay()
    {
        if (_syncBatch)
            return;
        Thread.Sleep(PostUpdateLedsDelayMs);
    }

    private string UpdateModeProtocol6(
        int deviceIndex,
        Device device,
        int modeIndex,
        Mode mode,
        uint speed,
        Color solidColor)
    {
        var colors = Array.Empty<(byte R, byte G, byte B)>();
        if (ModeNeedsModeSpecificColors(mode))
        {
            var built = BuildModeSpecificColors(mode, solidColor);
            colors = built.Select(c => (c.R, c.G, c.B)).ToArray();
        }

        var modeData = OpenRgbProtocol6Client.BuildModeDataProtocol6(
            mode.Name ?? $"Mode {modeIndex}",
            (uint)mode.Flags,
            mode.SpeedMin,
            mode.SpeedMax,
            mode.BrightnessMin,
            mode.BrightnessMax,
            mode.ColorMin,
            mode.ColorMax,
            speed,
            mode.SupportsBrightness ? mode.Brightness : 0,
            mode.SupportsDirection ? (uint)mode.Direction : 0,
            (uint)mode.ColorMode,
            colors);

        try
        {
            OpenRgbProtocol6Client.UpdateMode(
                Host, Port, Math.Max(_timeoutMs, 2000),
                deviceIndex, device.Name, modeIndex, modeData);

            var colorNote = colors.Length > 0 ? "+colors" : "";
            var speedNote = mode.SupportsSpeed ? $", speed={speed}" : "";
            return $"proto6 UpdateMode({mode.Name}{colorNote}{speedNote})";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[UnifiedRgb] proto6 UpdateMode({mode.Name}) failed: {ex.Message}; trying OpenRGB.NET");

            // Fallback: OpenRGB.NET ordinal UpdateMode (may mis-target on some servers).
            try
            {
                Color[]? netColors = colors.Length > 0
                    ? colors.Select(c => new Color(c.R, c.G, c.B)).ToArray()
                    : null;
                _client!.UpdateMode(
                    deviceIndex,
                    modeIndex,
                    mode.SupportsSpeed ? speed : null,
                    null,
                    netColors);
                return $"UpdateMode({mode.Name}) via OpenRGB.NET (proto6 failed: {ex.Message})";
            }
            catch (Exception ex2)
            {
                throw new InvalidOperationException(
                    $"UpdateMode({mode.Name}) failed: {ex.Message}; fallback: {ex2.Message}", ex);
            }
        }
    }

    private static (int Index, Mode Mode)? FindMode(Device device, string modeName)
    {
        if (device.Modes is null || device.Modes.Length == 0)
            return null;

        // Exact (ignore case)
        for (var i = 0; i < device.Modes.Length; i++)
        {
            var m = device.Modes[i];
            if (EffectModes.NamesMatch(m.Name, modeName))
                return (i, m);
        }

        // Contains either way
        for (var i = 0; i < device.Modes.Length; i++)
        {
            var m = device.Modes[i];
            var name = m.Name ?? "";
            if (name.Contains(modeName, StringComparison.OrdinalIgnoreCase) ||
                modeName.Contains(name, StringComparison.OrdinalIgnoreCase))
                return (i, m);
        }

        // Closest fallback so Sync all Breathing still does something useful.
        return FindClosestMode(device, modeName);
    }

    /// <summary>
    /// Prefer related effect names, then any speed-capable effect, then Static, then Direct.
    /// </summary>
    private static (int Index, Mode Mode)? FindClosestMode(Device device, string modeName)
    {
        if (device.Modes is null || device.Modes.Length == 0)
            return null;

        string[] PreferFor(string requested)
        {
            var r = requested.Trim();
            if (r.Contains("Breath", StringComparison.OrdinalIgnoreCase) ||
                r.Contains("Fade", StringComparison.OrdinalIgnoreCase) ||
                r.Contains("Pulse", StringComparison.OrdinalIgnoreCase))
                return ["Breath", "Fade", "Pulse", "Spectrum", "Cycle", "Wave"];
            if (r.Contains("Spectrum", StringComparison.OrdinalIgnoreCase) ||
                r.Contains("Rainbow", StringComparison.OrdinalIgnoreCase) ||
                r.Contains("Cycle", StringComparison.OrdinalIgnoreCase))
                return ["Spectrum", "Rainbow", "Cycle", "Wave", "Breath"];
            if (r.Contains("Wave", StringComparison.OrdinalIgnoreCase))
                return ["Wave", "Spectrum", "Rainbow", "Breath"];
            return [];
        }

        foreach (var needle in PreferFor(modeName))
        {
            for (var i = 0; i < device.Modes.Length; i++)
            {
                var name = device.Modes[i].Name ?? "";
                if (name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return (i, device.Modes[i]);
            }
        }

        for (var i = 0; i < device.Modes.Length; i++)
        {
            if (device.Modes[i].SupportsSpeed)
                return (i, device.Modes[i]);
        }

        for (var i = 0; i < device.Modes.Length; i++)
        {
            var name = device.Modes[i].Name ?? "";
            if (name.Contains("Static", StringComparison.OrdinalIgnoreCase))
                return (i, device.Modes[i]);
        }

        for (var i = 0; i < device.Modes.Length; i++)
        {
            var name = device.Modes[i].Name ?? "";
            if (name.Contains("Direct", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Custom", StringComparison.OrdinalIgnoreCase))
                return (i, device.Modes[i]);
        }

        return (0, device.Modes[0]);
    }

    /// <summary>
    /// When the OpenRGB device name is Cooler Master ARGB Gen2, send HID hardware mode.
    /// Throws if matching device but HID write fails on Windows (so UI shows the error).
    /// Non-Windows / non-CM devices return false and keep the OpenRGB path.
    /// </summary>
    private static bool TryApplyCmGen2HidEffect(
        string? deviceName,
        string modeName,
        RgbColor scaled,
        double speed01,
        double brightness01,
        out string note)
    {
        note = "";
        if (!CmArgbGen2HidController.IsCoolerMasterArgbGen2(deviceName))
            return false;

        if (CmArgbGen2HidController.TryApplyNamedMode(
                modeName, speed01, brightness01, scaled.R, scaled.G, scaled.B,
                out var statusOrError, out var isError))
        {
            note = statusOrError ?? "HID OK";
            return true;
        }

        if (!OperatingSystem.IsWindows())
            return false;

        throw new InvalidOperationException(
            statusOrError ?? "Cooler Master ARGB Gen2 HID mode failed.");
    }

    private const int PostUpdateLedsDelayMs = 50;

    /// <summary>
    /// Enter a writable lighting mode when the requested named mode is missing.
    /// Best-effort via protocol-6 UpdateMode; colors applied afterward via UpdateLeds.
    /// </summary>
    private string EnterWritableMode(
        int deviceIndex, Device device, Color solidColor, double speed01)
    {
        if (TryUpdateNamedMode(deviceIndex, device, solidColor, speed01, "Direct", out var note))
            return note;
        if (TryUpdateNamedMode(deviceIndex, device, solidColor, speed01, "Custom", out note))
            return note;

        try
        {
            _client!.SetCustomMode(deviceIndex);
            return "SetCustomMode";
        }
        catch
        {
            // Static-only boards reject SetCustomMode.
        }

        if (TryUpdateNamedMode(deviceIndex, device, solidColor, speed01, "Static", out note))
            return note;

        return "";
    }

    private bool TryUpdateNamedMode(
        int deviceIndex,
        Device device,
        Color solidColor,
        double speed01,
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
                var speed = MapSpeed01(mode, speed01);
                note = UpdateModeProtocol6(deviceIndex, device, i, mode, speed, solidColor);
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
        var modes = new List<ModeInfo>(d.Modes.Length);
        for (var i = 0; i < d.Modes.Length; i++)
        {
            var m = d.Modes[i];
            modes.Add(new ModeInfo
            {
                Index = i,
                Name = m.Name ?? $"Mode {i}",
                SupportsSpeed = m.SupportsSpeed,
                SpeedMin = m.SpeedMin,
                SpeedMax = m.SpeedMax,
                SupportsBrightness = m.SupportsBrightness,
                HasPerLedColor = m.Flags.HasFlag(ModeFlags.HasPerLedColor) || m.ColorMode == ColorMode.PerLed,
                HasModeSpecificColor = m.Flags.HasFlag(ModeFlags.HasModeSpecificColor) || m.ColorMode == ColorMode.ModeSpecific,
                ColorMode = m.ColorMode.ToString()
            });
        }

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
            ActiveModeSupportsSpeed = mode?.SupportsSpeed == true,
            Brightness = mode?.SupportsBrightness == true ? mode.Brightness : null,
            BrightnessMin = mode?.SupportsBrightness == true ? mode.BrightnessMin : null,
            BrightnessMax = mode?.SupportsBrightness == true ? mode.BrightnessMax : null,
            Speed = mode?.SupportsSpeed == true ? mode.Speed : null,
            SpeedMin = mode?.SupportsSpeed == true ? mode.SpeedMin : null,
            SpeedMax = mode?.SupportsSpeed == true ? mode.SpeedMax : null,
            CurrentColor = current,
            Modes = modes,
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
