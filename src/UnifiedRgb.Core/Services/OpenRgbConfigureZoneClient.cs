using System.Buffers.Binary;
using System.Text;

namespace UnifiedRgb.Core.Services;

/// <summary>
/// ConfigureZone (packet 1003) helpers for Cooler Master ARGB Gen2 channel sizing.
/// TCP handshake / unique-ID addressing lives in <see cref="OpenRgbProtocol6Client"/>
/// (protocol 6); this type keeps Zone Data builders used by that path.
/// OpenRGB.NET 3.1.1 only speaks protocol 4 and has no ConfigureZone API.
/// </summary>
internal static class OpenRgbConfigureZoneClient
{
    public const uint ZoneFlagManuallyConfigurableSize = 1u << 1;
    public const uint ZoneFlagManuallyConfiguredSize = 1u << 12;
    public const int ZoneTypeLinear = 1;

    /// <summary>
    /// Open a short-lived SDK connection, negotiate protocol 6 (or the server max),
    /// and send ConfigureZone so Cooler Master ARGB Gen2 channels can go 0→N LEDs
    /// without the OpenRGB GUI "Edit Zone" control.
    /// </summary>
    public static void ConfigureZone(
        string host,
        int port,
        int timeoutMs,
        int deviceIndex,
        int zoneIndex,
        OpenRgbZoneConfig zone,
        string? preferredDeviceName = null)
    {
        OpenRgbProtocol6Client.ConfigureZone(
            host, port, timeoutMs, deviceIndex, preferredDeviceName, zoneIndex, zone);
    }

    public static byte[] BuildConfigureZonePayload(int zoneIndex, OpenRgbZoneConfig zone, uint protocol)
    {
        var zoneData = BuildZoneData(zone, protocol);
        // data_size MUST equal the entire payload length, including the data_size field itself.
        var payloadLen = 4 + 4 + zoneData.Length;
        var payload = new byte[payloadLen];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), (uint)payloadLen);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), zoneIndex);
        Buffer.BlockCopy(zoneData, 0, payload, 8, zoneData.Length);
        return payload;
    }

    public static byte[] BuildZoneData(OpenRgbZoneConfig zone, uint protocol)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        var name = EncodeCString(string.IsNullOrWhiteSpace(zone.Name) ? "Zone" : zone.Name);
        w.Write((ushort)name.Length);
        w.Write(name);

        w.Write(zone.Type);
        w.Write(zone.LedsMin);
        w.Write(zone.LedsMax);
        w.Write(zone.LedsCount);

        w.Write((ushort)0); // matrix_map length = 0

        if (protocol >= 4)
            w.Write((ushort)0); // num_segments

        if (protocol >= 5)
            w.Write(zone.Flags);

        if (protocol >= 6)
        {
            w.Write(0);            // active_mode
            w.Write((ushort)0);    // num_modes
            var display = EncodeCString("");
            w.Write((ushort)display.Length);
            w.Write(display);
        }

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Protocol 6: uint32 count followed by unique IDs in list order (matches OpenRGB.NET indices).
    /// Kept for unit-style callers / older ResolveDeviceId usage.
    /// </summary>
    internal static uint ResolveDeviceId(byte[] countPayload, int deviceIndex)
    {
        var ids = OpenRgbProtocol6Client.ParseUniqueIds(countPayload);
        if (deviceIndex < 0 || deviceIndex >= ids.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceIndex),
                $"Device {deviceIndex} is out of range (server has {ids.Length} controller(s)).");
        }
        return ids[deviceIndex];
    }

    private static byte[] EncodeCString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        var result = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        return result;
    }
}

internal sealed class OpenRgbZoneConfig
{
    public string Name { get; init; } = "Zone";
    public int Type { get; init; } = OpenRgbConfigureZoneClient.ZoneTypeLinear;
    public uint LedsMin { get; init; }
    public uint LedsMax { get; init; } = 72;
    public uint LedsCount { get; init; }
    public uint Flags { get; init; } =
        OpenRgbConfigureZoneClient.ZoneFlagManuallyConfigurableSize |
        OpenRgbConfigureZoneClient.ZoneFlagManuallyConfiguredSize;
}
