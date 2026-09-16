using HidSharp;

namespace UnifiedRgb.Core.Services;

/// <summary>
/// Windows HID control for Cooler Master ARGB Gen 2 A1 V2 (VID 0x2516 / PID 0x01C9).
/// OpenRGB UpdateMode ACKs but often leaves Spectrum/rainbow; raw HID HW_MODE_SETUP works.
/// Packet layout and mode bytes from OpenRGB CMARGBGen2A1Controller.h / verified on Marcus_PC.
/// </summary>
public static class CmArgbGen2HidController
{
    public const int VendorId = 0x2516;
    public const int ProductId = 0x01C9;

    // CM_ARGB_GEN2_A1_*_MODE from CMARGBGen2A1Controller.h
    public const byte HwModeSpectrum = 0x00;
    public const byte HwModeStatic = 0x01;
    public const byte HwModeReload = 0x02;
    public const byte HwModeRecoil = 0x03;
    public const byte HwModeBreathing = 0x04;
    public const byte HwModeRefill = 0x05;
    public const byte HwModeDemo = 0x06;
    public const byte HwModeFillFlow = 0x07;
    public const byte HwModeRainbow = 0x08;
    public const byte HwModeOff = 0x09;
    public const byte HwModeCustom = 0xC0;

    public const byte SpeedMin = 0x00;
    public const byte SpeedHalf = 0x02;
    public const byte SpeedMax = 0x04;
    public const byte BrightnessMin = 0x00;
    public const byte BrightnessMax = 0xFF;

    private const byte Cmd = 0x80;
    private const byte Write = 0x02;
    private const byte LightningControl = 0x01;
    private const byte HwModeSetup = 0x03;
    private const byte ApplyChanges = 0xB0;
    private const byte ChannelAll = 0xFF;
    private const byte SubchannelAll = 0xFF;

    private const int PacketLengthWithReportId = 65; // report id 0 + 64 payload
    private const int PacketLengthPayloadOnly = 64;
    /// <summary>Delay between HID packets and between double Static sends for stickiness.</summary>
    public const int InterPacketDelayMs = 70;

    /// <summary>Match OpenRGB device name for Cooler Master ARGB Gen 2 hubs.</summary>
    public static bool IsCoolerMasterArgbGen2(string? deviceName) =>
        !string.IsNullOrWhiteSpace(deviceName) &&
        deviceName.Contains("Cooler Master ARGB", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Map a UI / OpenRGB mode name to a CM Gen2 hardware mode byte.
    /// Direct/Custom map to Static (solid) — we never follow with OpenRGB Direct.
    /// Returns null when the name is not a known CM hardware effect.
    /// </summary>
    public static byte? TryMapModeName(string? modeName)
    {
        if (string.IsNullOrWhiteSpace(modeName))
            return HwModeStatic;

        var n = modeName.Trim();
        if (Contains(n, "off")) return HwModeOff;
        if (Contains(n, "spectrum")) return HwModeSpectrum;
        if (Contains(n, "rainbow")) return HwModeRainbow;
        if (Contains(n, "breath")) return HwModeBreathing;
        if (Contains(n, "reload")) return HwModeReload;
        if (Contains(n, "recoil")) return HwModeRecoil;
        if (Contains(n, "refill")) return HwModeRefill;
        if (Contains(n, "demo")) return HwModeDemo;
        if (Contains(n, "fill") && Contains(n, "flow")) return HwModeFillFlow;
        if (Contains(n, "fill flow")) return HwModeFillFlow;
        if (Contains(n, "static")) return HwModeStatic;
        // Direct / Custom / per-LED software modes → solid Static HID (no OpenRGB Direct).
        if (Contains(n, "direct") || Contains(n, "custom")) return HwModeStatic;
        return null;
    }

    /// <summary>Map UI speed 0–100 to CM HID speed byte (0x00–0x04).</summary>
    public static byte MapSpeed01ToHw(double speed01)
    {
        speed01 = Math.Clamp(speed01, 0, 1);
        return (byte)Math.Round(SpeedMin + speed01 * (SpeedMax - SpeedMin));
    }

    /// <summary>Map brightness 0–1 to CM HID brightness byte.</summary>
    public static byte MapBrightness01ToHw(double brightness01) =>
        (byte)Math.Round(Math.Clamp(brightness01, 0, 1) * BrightnessMax);

    /// <summary>Send HID Static solid color on Windows (existing solid path).</summary>
    public static bool TrySetStaticColor(byte r, byte g, byte b, out string? error) =>
        TrySetStaticColor(r, g, b, BrightnessMax, apply: true, out error);

    public static bool TrySetStaticColor(byte r, byte g, byte b, byte brightness, bool apply, out string? error) =>
        TrySetHardwareMode(HwModeStatic, SpeedHalf, brightness, r, g, b, apply, out error);

    /// <summary>
    /// Send HID hardware mode (LIGHTNING_CONTROL → HW_MODE_SETUP → optional APPLY_CHANGES).
    /// Same packet layout as OpenRGB CMARGBGen2A1Controller::SetMode.
    /// </summary>
    public static bool TrySetHardwareMode(
        byte mode,
        byte speed,
        byte brightness,
        byte r,
        byte g,
        byte b,
        out string? error) =>
        TrySetHardwareMode(mode, speed, brightness, r, g, b, apply: true, out error);

    public static bool TrySetHardwareMode(
        byte mode,
        byte speed,
        byte brightness,
        byte r,
        byte g,
        byte b,
        bool apply,
        out string? error)
    {
        error = null;

        if (!OperatingSystem.IsWindows())
        {
            error = "CM Gen2 HID modes are only implemented for Windows.";
            return false;
        }

        try
        {
            var candidates = DeviceList.Local
                .GetHidDevices(VendorId, ProductId)
                .Where(IsPreferredInterface)
                .OrderBy(InterfaceSortKey)
                .ToList();

            if (candidates.Count == 0)
            {
                error = $"No HID device found for Cooler Master ARGB Gen2 (VID {VendorId:X4} PID {ProductId:X4}, iface 0/1).";
                return false;
            }

            var anySuccess = false;
            Exception? lastWriteError = null;
            var openFailures = 0;

            foreach (var device in candidates)
            {
                try
                {
                    if (!device.TryOpen(out var stream))
                    {
                        openFailures++;
                        continue;
                    }

                    using (stream)
                    {
                        WriteModeSequence(stream, device, mode, speed, brightness, r, g, b, apply);
                    }

                    anySuccess = true;
                }
                catch (Exception ex)
                {
                    lastWriteError = ex;
                }
            }

            if (anySuccess)
            {
                error = null;
                return true;
            }

            error = lastWriteError is null
                ? openFailures > 0
                    ? "Could not open any Cooler Master ARGB Gen2 HID interface for write."
                    : "Could not write to any Cooler Master ARGB Gen2 HID interface."
                : $"HID write failed on all interfaces: {lastWriteError.Message}";
            return false;
        }
        catch (Exception ex)
        {
            error = $"HID enumeration failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>Resolve mode name + UI speed/brightness/color into a HID write.</summary>
    public static bool TryApplyNamedMode(
        string? modeName,
        double speed01,
        double brightness01,
        byte r,
        byte g,
        byte b,
        out string? statusOrError,
        out bool isError)
    {
        isError = false;
        var mapped = TryMapModeName(modeName);
        if (mapped is null)
        {
            // Unknown name → Static solid (preserves old solid behavior).
            mapped = HwModeStatic;
        }

        var speed = MapSpeed01ToHw(speed01);
        var brightness = MapBrightness01ToHw(brightness01);
        var modeByte = mapped.Value;
        var modeLabel = ModeByteLabel(modeByte);

        if (!TrySetHardwareMode(modeByte, speed, brightness, r, g, b, out var error))
        {
            isError = true;
            statusOrError = error ?? "CM Gen2 HID mode failed.";
            return false;
        }

        statusOrError = $"HID {modeLabel} (mode=0x{modeByte:X2}, speed={speed}, bright={brightness})";
        return true;
    }

    public static string ModeByteLabel(byte mode) => mode switch
    {
        HwModeSpectrum => "Spectrum",
        HwModeStatic => "Static",
        HwModeReload => "Reload",
        HwModeRecoil => "Recoil",
        HwModeBreathing => "Breathing",
        HwModeRefill => "Refill",
        HwModeDemo => "Demo",
        HwModeFillFlow => "Fill Flow",
        HwModeRainbow => "Rainbow",
        HwModeOff => "Off",
        HwModeCustom => "Custom",
        _ => $"Mode0x{mode:X2}"
    };

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static void WriteModeSequence(
        HidStream stream,
        HidDevice device,
        byte mode,
        byte speed,
        byte brightness,
        byte r,
        byte g,
        byte b,
        bool apply)
    {
        // Leave software/direct mode if the hub was previously in CUSTOM (OpenRGB Direct).
        WritePacketFlexible(stream, device, BuildLightningControl);
        Thread.Sleep(InterPacketDelayMs);

        WritePacketFlexible(stream, device, len => BuildHwModeSetup(len, mode, speed, brightness, r, g, b));
        Thread.Sleep(InterPacketDelayMs);

        if (apply)
        {
            WritePacketFlexible(stream, device, BuildApplyChanges);
            Thread.Sleep(InterPacketDelayMs);
        }
    }

    /// <summary>
    /// Prefer 65-byte (report id 0 + 64) when the stack allows it; if max output is 64,
    /// try 64-byte payload-only. On write failure with the first size, retry the other.
    /// </summary>
    private static void WritePacketFlexible(
        HidStream stream,
        HidDevice device,
        Func<int, byte[]> buildPacket)
    {
        var lengths = ResolvePacketLengths(device);
        Exception? last = null;

        foreach (var len in lengths)
        {
            try
            {
                var packet = buildPacket(len);
                stream.Write(packet);
                stream.Flush();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw last ?? new InvalidOperationException("HID write failed for all packet lengths.");
    }

    private static int[] ResolvePacketLengths(HidDevice device)
    {
        int maxOut;
        try
        {
            maxOut = device.GetMaxOutputReportLength();
        }
        catch
        {
            maxOut = PacketLengthWithReportId;
        }

        if (maxOut == PacketLengthPayloadOnly)
            return new[] { PacketLengthPayloadOnly, PacketLengthWithReportId };

        if (maxOut > 0 && maxOut < PacketLengthPayloadOnly)
            return new[] { maxOut, PacketLengthWithReportId, PacketLengthPayloadOnly };

        return new[] { PacketLengthWithReportId, PacketLengthPayloadOnly };
    }

    private static bool IsPreferredInterface(HidDevice device)
    {
        var path = device.DevicePath ?? "";
        if (path.Contains("MI_02", StringComparison.OrdinalIgnoreCase))
            return false;
        if (path.Contains("MI_00", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("MI_01", StringComparison.OrdinalIgnoreCase))
            return true;

        return !path.Contains("MI_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>MI_01 first (drives LEDs on Marcus_PC), then MI_00, then unknown.</summary>
    private static int InterfaceSortKey(HidDevice device)
    {
        var path = device.DevicePath ?? "";
        if (path.Contains("MI_01", StringComparison.OrdinalIgnoreCase)) return 0;
        if (path.Contains("MI_00", StringComparison.OrdinalIgnoreCase)) return 1;
        return 2;
    }

    private static byte[] BuildLightningControl(int length)
    {
        var p = new byte[length];
        var o = length >= PacketLengthWithReportId ? 1 : 0;
        if (o == 1)
            p[0] = 0;
        p[o] = Cmd;
        p[o + 1] = LightningControl;
        p[o + 2] = Write;
        return p;
    }

    private static byte[] BuildHwModeSetup(
        int length,
        byte mode,
        byte speed,
        byte brightness,
        byte r,
        byte g,
        byte b)
    {
        var p = new byte[length];
        var o = length >= PacketLengthWithReportId ? 1 : 0;
        if (o == 1)
            p[0] = 0;
        p[o] = Cmd;
        p[o + 1] = HwModeSetup;
        p[o + 2] = Write;
        p[o + 3] = ChannelAll;
        p[o + 4] = SubchannelAll;
        p[o + 5] = mode;
        p[o + 6] = speed;
        p[o + 7] = brightness;
        p[o + 8] = r;
        p[o + 9] = g;
        p[o + 10] = b;
        return p;
    }

    private static byte[] BuildApplyChanges(int length)
    {
        var p = new byte[length];
        var o = length >= PacketLengthWithReportId ? 1 : 0;
        if (o == 1)
            p[0] = 0;
        p[o] = Cmd;
        p[o + 1] = ApplyChanges;
        p[o + 2] = Write;
        return p;
    }
}
