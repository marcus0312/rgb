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

    private const int PacketLength = 65;
    private const int InterPacketDelayMs = 70;

    /// <summary>Match OpenRGB device name for Cooler Master ARGB Gen 2 hubs.</summary>
    public static bool IsCoolerMasterArgbGen2(string? deviceName) =>
        !string.IsNullOrWhiteSpace(deviceName) &&
        deviceName.Contains("Cooler Master ARGB", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Send HID Static solid color on Windows. Prefers interface 0 or 1 (skips MI_02 mouse).
    /// Returns false when not Windows, device missing, or all interfaces fail to write.
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

            Exception? lastWriteError = null;
            foreach (var device in candidates)
            {
                try
                {
                    if (!device.TryOpen(out var stream))
                        continue;

                    using (stream)
                    {
                        WritePacket(stream, BuildLightningControl());
                        Thread.Sleep(InterPacketDelayMs);

                        WritePacket(stream, BuildHwModeSetupStatic(r, g, b, brightness));
                        Thread.Sleep(InterPacketDelayMs);

                        if (apply)
                        {
                            WritePacket(stream, BuildApplyChanges());
                            Thread.Sleep(InterPacketDelayMs);
                        }
                    }

                    error = null;
                    return true;
                }
                catch (Exception ex)
                {
                    lastWriteError = ex;
                }
            }

            error = lastWriteError is null
                ? "Could not open any Cooler Master ARGB Gen2 HID interface for write."
                : $"HID write failed: {lastWriteError.Message}";
            return false;
        }
        catch (Exception ex)
        {
            error = $"HID enumeration failed: {ex.Message}";
            return false;
        }
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

    private static int InterfaceSortKey(HidDevice device)
    {
        var path = device.DevicePath ?? "";
        if (path.Contains("MI_00", StringComparison.OrdinalIgnoreCase)) return 0;
        if (path.Contains("MI_01", StringComparison.OrdinalIgnoreCase)) return 1;
        return 2;
    }

    private static void WritePacket(HidStream stream, byte[] packet)
    {
        stream.Write(packet);
        stream.Flush();
    }

    private static byte[] BuildLightningControl()
    {
        var p = new byte[PacketLength];
        p[0] = 0; // report id
        p[1] = Cmd;
        p[2] = LightningControl;
        p[3] = Write;
        return p;
    }

    private static byte[] BuildHwModeSetupStatic(byte r, byte g, byte b, byte brightness)
    {
        var p = new byte[PacketLength];
        p[0] = 0;
        p[1] = Cmd;
        p[2] = HwModeSetup;
        p[3] = Write;
        p[4] = ChannelAll;
        p[5] = SubchannelAll;
        p[6] = StaticMode;
        p[7] = DefaultSpeed;
        p[8] = brightness;
        p[9] = r;
        p[10] = g;
        p[11] = b;
        return p;
    }

    private static byte[] BuildApplyChanges()
    {
        var p = new byte[PacketLength];
        p[0] = 0;
        p[1] = Cmd;
        p[2] = ApplyChanges;
        p[3] = Write;
        return p;
    }
}
