using HidSharp;

namespace UnifiedRgb.Core.Services;

/// <summary>
/// Windows HID Static control for Cooler Master ARGB Gen 2 A1 V2 (VID 0x2516 / PID 0x01C9).
/// OpenRGB UpdateMode ACKs but often leaves Spectrum/rainbow; raw HID Static works.
/// Packet layout from OpenRGB CMARGBGen2A1Controller.h / verified on Marcus_PC.
/// </summary>
public static class CmArgbGen2HidController
{
    public const int VendorId = 0x2516;
    public const int ProductId = 0x01C9;

    private const byte Cmd = 0x80;
    private const byte Write = 0x02;
    private const byte LightningControl = 0x01;
    private const byte HwModeSetup = 0x03;
    private const byte ApplyChanges = 0xB0;
    private const byte ChannelAll = 0xFF;
    private const byte SubchannelAll = 0xFF;
    private const byte StaticMode = 0x01;
    private const byte DefaultSpeed = 0x02;
    private const byte FullBrightness = 0xFF;

    private const int PacketLengthWithReportId = 65; // report id 0 + 64 payload
    private const int PacketLengthPayloadOnly = 64;
    /// <summary>Delay between HID packets and between double Static sends for stickiness.</summary>
    public const int InterPacketDelayMs = 70;

    /// <summary>Match OpenRGB device name for Cooler Master ARGB Gen 2 hubs.</summary>
    public static bool IsCoolerMasterArgbGen2(string? deviceName) =>
        !string.IsNullOrWhiteSpace(deviceName) &&
        deviceName.Contains("Cooler Master ARGB", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Send HID Static solid color on Windows. Writes to all preferred interfaces
    /// (MI_01 then MI_00; skips MI_02 mouse). Succeeds if any interface accepts the write.
    /// </summary>
    public static bool TrySetStaticColor(byte r, byte g, byte b, out string? error) =>
        TrySetStaticColor(r, g, b, FullBrightness, apply: true, out error);

    public static bool TrySetStaticColor(byte r, byte g, byte b, byte brightness, bool apply, out string? error)
    {
        error = null;

        if (!OperatingSystem.IsWindows())
        {
            error = "CM Gen2 HID Static is only implemented for Windows.";
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

            // Write to every preferred interface. MI_00 may accept writes without driving LEDs;
            // MI_01 is tried first and both are always attempted when present.
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
                        WriteStaticSequence(stream, device, r, g, b, brightness, apply);
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

    private static void WriteStaticSequence(
        HidStream stream,
        HidDevice device,
        byte r,
        byte g,
        byte b,
        byte brightness,
        bool apply)
    {
        WritePacketFlexible(stream, device, BuildLightningControl);
        Thread.Sleep(InterPacketDelayMs);

        WritePacketFlexible(stream, device, len => BuildHwModeSetupStatic(len, r, g, b, brightness));
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

        // Prefer report-id-prefixed 65 when the device advertises >= 65 (or unknown).
        // When max is exactly 64, try 64 first then 65 as fallback (some stacks are picky).
        if (maxOut == PacketLengthPayloadOnly)
            return new[] { PacketLengthPayloadOnly, PacketLengthWithReportId };

        if (maxOut > 0 && maxOut < PacketLengthPayloadOnly)
            return new[] { maxOut, PacketLengthWithReportId, PacketLengthPayloadOnly };

        return new[] { PacketLengthWithReportId, PacketLengthPayloadOnly };
    }

    private static bool IsPreferredInterface(HidDevice device)
    {
        var path = device.DevicePath ?? "";
        // Avoid MI_02 (mouse composite). Prefer MI_00 / MI_01 (usage_page 0xFF00/0xFF01).
        if (path.Contains("MI_02", StringComparison.OrdinalIgnoreCase))
            return false;
        if (path.Contains("MI_00", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("MI_01", StringComparison.OrdinalIgnoreCase))
            return true;

        // If path has no MI_ tag, still try (some stacks omit it).
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
        // length 65: [0]=report id, [1..]=payload. length 64: payload starts at [0].
        var o = length >= PacketLengthWithReportId ? 1 : 0;
        if (o == 1)
            p[0] = 0;
        p[o] = Cmd;
        p[o + 1] = LightningControl;
        p[o + 2] = Write;
        return p;
    }

    private static byte[] BuildHwModeSetupStatic(int length, byte r, byte g, byte b, byte brightness)
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
        p[o + 5] = StaticMode;
        p[o + 6] = DefaultSpeed;
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
