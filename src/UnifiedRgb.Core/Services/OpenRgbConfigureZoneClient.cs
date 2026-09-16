using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace UnifiedRgb.Core.Services;

/// <summary>
/// Raw OpenRGB SDK helper for <c>NET_PACKET_ID_RGBCONTROLLER_CONFIGUREZONE = 1003</c>
/// (protocol 5/6). OpenRGB.NET 3.1.1 only speaks protocol 4 and has no ConfigureZone
/// API; its private <c>OpenRgbConnection.Send</c> cannot be reused because the server
/// would parse Zone Data at protocol 4 (no <c>zone_flags</c>), so
/// <c>ZONE_FLAG_MANUALLY_CONFIGURED_SIZE</c> would never apply.
/// </summary>
internal static class OpenRgbConfigureZoneClient
{
    public const uint PacketRequestControllerCount = 0;
    public const uint PacketAck = 10;
    public const uint PacketRequestProtocolVersion = 40;
    public const uint PacketSetClientName = 50;
    public const uint PacketSetServerName = 51;
    public const uint PacketConfigureZone = 1003;

    public const uint ZoneFlagManuallyConfigurableSize = 1u << 1;
    public const uint ZoneFlagManuallyConfiguredSize = 1u << 12;

    public const uint ClientProtocolVersion = 6;
    public const int ZoneTypeLinear = 1;

    public const uint AckOk = 0;
    public const uint AckInvalidData = 5;

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
        OpenRgbZoneConfig zone)
    {
        if (timeoutMs <= 0)
            timeoutMs = 2000;

        using var tcp = new TcpClient();
        tcp.NoDelay = true;
        tcp.ReceiveTimeout = timeoutMs;
        tcp.SendTimeout = timeoutMs;
        Connect(tcp, host, port, timeoutMs);

        using var stream = tcp.GetStream();
        stream.ReadTimeout = timeoutMs;
        stream.WriteTimeout = timeoutMs;

        SendPacket(stream, deviceId: 0, PacketSetClientName, EncodeCString("UnifiedRgb-ConfigureZone"));
        SendPacket(stream, deviceId: 0, PacketRequestProtocolVersion, BitConverter.GetBytes(ClientProtocolVersion));

        var negotiated = WaitForProtocolVersion(stream, timeoutMs);
        // Server also sends SET_SERVER_NAME (+ ACK on protocol 6). Drain those.
        DrainOptional(stream, TimeSpan.FromMilliseconds(Math.Min(400, timeoutMs)));

        if (negotiated < 5)
        {
            throw new InvalidOperationException(
                $"OpenRGB SDK protocol {negotiated} cannot ConfigureZone with size flags " +
                "(need protocol 5+ / OpenRGB 1.0). ResizeZone is a no-op for CM Gen2.");
        }

        uint deviceId = (uint)deviceIndex;
        if (negotiated >= 6)
        {
            SendPacket(stream, deviceId: 0, PacketRequestControllerCount, Array.Empty<byte>());
            var countPayload = WaitForPacket(stream, PacketRequestControllerCount, timeoutMs);
            deviceId = ResolveDeviceId(countPayload, deviceIndex);
        }

        var payload = BuildConfigureZonePayload(zoneIndex, zone, negotiated);
        SendPacket(stream, deviceId, PacketConfigureZone, payload);

        if (negotiated >= 6)
        {
            var ack = WaitForAck(stream, PacketConfigureZone, timeoutMs);
            if (ack != AckOk)
            {
                throw new InvalidOperationException(
                    $"ConfigureZone ACK status {ack} (5=INVALID_DATA usually means data_size " +
                    "did not equal the full packet payload).");
            }
        }
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

    internal static uint ResolveDeviceId(byte[] countPayload, int deviceIndex)
    {
        if (countPayload.Length < 4)
            throw new InvalidOperationException("Controller-count reply was empty.");

        var count = BinaryPrimitives.ReadUInt32LittleEndian(countPayload.AsSpan(0, 4));
        if (deviceIndex < 0 || deviceIndex >= count)
            throw new ArgumentOutOfRangeException(nameof(deviceIndex),
                $"Device {deviceIndex} is out of range (server has {count} controller(s)).");

        // Protocol 6: uint32 count followed by unique IDs in list order (matches OpenRGB.NET indices).
        var idsBytes = countPayload.Length - 4;
        if (idsBytes >= 4 * (int)count)
        {
            var offset = 4 + (4 * deviceIndex);
            return BinaryPrimitives.ReadUInt32LittleEndian(countPayload.AsSpan(offset, 4));
        }

        return (uint)deviceIndex;
    }

    private static void Connect(TcpClient tcp, string host, int port, int timeoutMs)
    {
        var task = tcp.ConnectAsync(host, port);
        if (!task.Wait(timeoutMs))
            throw new TimeoutException($"Timed out connecting to OpenRGB SDK at {host}:{port}.");
        task.GetAwaiter().GetResult();
    }

    private static uint WaitForProtocolVersion(NetworkStream stream, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var remaining = (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds);
            var pkt = ReadPacket(stream, remaining);
            if (pkt.Id == PacketRequestProtocolVersion && pkt.Payload.Length >= 4)
            {
                var server = BinaryPrimitives.ReadUInt32LittleEndian(pkt.Payload.AsSpan(0, 4));
                return Math.Min(ClientProtocolVersion, server);
            }
        }

        throw new TimeoutException("OpenRGB SDK did not return a protocol version (need 1.0+ / protocol 5+).");
    }

    private static byte[] WaitForPacket(NetworkStream stream, uint expectedId, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var remaining = (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds);
            var pkt = ReadPacket(stream, remaining);
            if (pkt.Id == expectedId)
                return pkt.Payload;
            // Skip ACK / server-name / device-list-updated noise.
        }

        throw new TimeoutException($"Timed out waiting for OpenRGB packet {expectedId}.");
    }

    private static uint WaitForAck(NetworkStream stream, uint forPacketId, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var remaining = (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds);
            var pkt = ReadPacket(stream, remaining);
            if (pkt.Id != PacketAck || pkt.Payload.Length < 8)
                continue;
            var acked = BinaryPrimitives.ReadUInt32LittleEndian(pkt.Payload.AsSpan(0, 4));
            if (acked != forPacketId)
                continue;
            return BinaryPrimitives.ReadUInt32LittleEndian(pkt.Payload.AsSpan(4, 4));
        }

        throw new TimeoutException("Timed out waiting for ConfigureZone ACK from OpenRGB.");
    }

    private static void DrainOptional(NetworkStream stream, TimeSpan window)
    {
        var previous = stream.ReadTimeout;
        try
        {
            stream.ReadTimeout = Math.Max(50, (int)window.TotalMilliseconds);
            var deadline = DateTime.UtcNow + window;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    _ = ReadPacket(stream, stream.ReadTimeout);
                }
                catch (IOException)
                {
                    break;
                }
                catch (TimeoutException)
                {
                    break;
                }
            }
        }
        finally
        {
            stream.ReadTimeout = previous;
        }
    }

    private static void SendPacket(NetworkStream stream, uint deviceId, uint packetId, byte[] payload)
    {
        var header = new byte[16];
        header[0] = (byte)'O';
        header[1] = (byte)'R';
        header[2] = (byte)'G';
        header[3] = (byte)'B';
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), deviceId);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), packetId);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)payload.Length);
        stream.Write(header, 0, header.Length);
        if (payload.Length > 0)
            stream.Write(payload, 0, payload.Length);
        stream.Flush();
    }

    private static (uint DeviceId, uint Id, byte[] Payload) ReadPacket(NetworkStream stream, int timeoutMs)
    {
        var previous = stream.ReadTimeout;
        stream.ReadTimeout = Math.Max(1, timeoutMs);
        try
        {
            var header = ReadExact(stream, 16);
            if (header[0] != (byte)'O' || header[1] != (byte)'R' || header[2] != (byte)'G' || header[3] != (byte)'B')
                throw new InvalidOperationException("Invalid OpenRGB packet magic (expected ORGB).");

            var deviceId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
            var id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
            if (size > 8 * 1024 * 1024)
                throw new InvalidOperationException($"OpenRGB packet too large ({size} bytes).");

            var payload = size == 0 ? Array.Empty<byte>() : ReadExact(stream, (int)size);
            return (deviceId, id, payload);
        }
        finally
        {
            stream.ReadTimeout = previous;
        }
    }

    private static byte[] ReadExact(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var n = stream.Read(buffer, offset, count - offset);
            if (n <= 0)
                throw new IOException("OpenRGB SDK connection closed while reading a packet.");
            offset += n;
        }

        return buffer;
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
