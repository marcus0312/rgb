using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace UnifiedRgb.Core.Services;

/// <summary>
/// Raw OpenRGB SDK client for protocol 6 (unique controller IDs).
/// OpenRGB.NET 3.1.1 max protocol is 4 and addresses devices by ordinal 0..n-1;
/// protocol-6 servers assign stable unique IDs (e.g. [4,5,6,7]) and ignore / mis-target
/// ordinal UpdateLeds. This helper negotiates protocol 6 and uses unique IDs.
///
/// Device backends (do not treat them like CM HID):
/// - ASRock Polychrome: no Direct mode; Static is per-LED (color_mode PER_LED) —
///   enter Static best-effort, then UpdateLeds for all zone LEDs.
/// - GALAX GPU: Direct + UpdateLeds (typically 1 LED).
/// - Logitech G502: Direct + UpdateLeds; quit Logitech G HUB (conflicts with OpenRGB).
/// - Cooler Master ARGB Gen2: HID Static only — callers must not use this path for CM.
/// </summary>
internal static class OpenRgbProtocol6Client
{
    public const uint PacketRequestControllerCount = 0;
    public const uint PacketRequestControllerData = 1;
    public const uint PacketAck = 10;
    public const uint PacketRequestProtocolVersion = 40;
    public const uint PacketSetClientName = 50;
    public const uint PacketSetServerName = 51;
    public const uint PacketConfigureZone = 1003;
    public const uint PacketUpdateLeds = 1050;
    public const uint PacketUpdateZoneLeds = 1051;
    public const uint PacketSetCustomMode = 1100;
    public const uint PacketUpdateMode = 1101;

    public const uint ClientProtocolVersion = 6;
    public const uint AckOk = 0;

    /// <summary>
    /// Apply solid colors to all LEDs on a controller via UPDATELEDS (1050) using the
    /// protocol-6 unique ID resolved from OpenRGB.NET ordinal (+ preferred name match).
    /// </summary>
    public static void UpdateLeds(
        string host,
        int port,
        int timeoutMs,
        int ordinalIndex,
        string? preferredDeviceName,
        ReadOnlySpan<(byte R, byte G, byte B)> colors)
    {
        if (colors.Length == 0)
            throw new ArgumentException("UpdateLeds requires at least one color.", nameof(colors));

        using var session = Session.Connect(host, port, timeoutMs, "UnifiedRgb-UpdateLeds");
        var uniqueId = session.ResolveUniqueId(ordinalIndex, preferredDeviceName);
        var payload = BuildUpdateLedsPayload(colors);
        session.SendPacket(uniqueId, PacketUpdateLeds, payload);
        session.ExpectAckOk(PacketUpdateLeds);
    }

    /// <summary>
    /// Apply colors to one zone via UPDATEZONELEDS (1051) with protocol-6 unique ID.
    /// </summary>
    public static void UpdateZoneLeds(
        string host,
        int port,
        int timeoutMs,
        int ordinalIndex,
        string? preferredDeviceName,
        int zoneIndex,
        ReadOnlySpan<(byte R, byte G, byte B)> colors)
    {
        if (colors.Length == 0)
            throw new ArgumentException("UpdateZoneLeds requires at least one color.", nameof(colors));
        if (zoneIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(zoneIndex));

        using var session = Session.Connect(host, port, timeoutMs, "UnifiedRgb-UpdateZoneLeds");
        var uniqueId = session.ResolveUniqueId(ordinalIndex, preferredDeviceName);
        var payload = BuildUpdateZoneLedsPayload(zoneIndex, colors);
        session.SendPacket(uniqueId, PacketUpdateZoneLeds, payload);
        session.ExpectAckOk(PacketUpdateZoneLeds);
    }

    /// <summary>
    /// Enter a mode via UPDATEMODE (1101) with protocol-6 unique ID.
    /// Mode Data must match the negotiated protocol (mode_value omitted for protocol 6).
    /// Prefer OpenRGB.NET for mode enter when it works; use this when ordinal UpdateMode
    /// targets the wrong controller on protocol-6 servers.
    /// </summary>
    public static void UpdateMode(
        string host,
        int port,
        int timeoutMs,
        int ordinalIndex,
        string? preferredDeviceName,
        int modeIndex,
        byte[] modeData)
    {
        if (modeData is null || modeData.Length == 0)
            throw new ArgumentException("Mode Data is required.", nameof(modeData));
        if (modeIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(modeIndex));

        using var session = Session.Connect(host, port, timeoutMs, "UnifiedRgb-UpdateMode");
        var uniqueId = session.ResolveUniqueId(ordinalIndex, preferredDeviceName);
        var payload = BuildUpdateModePayload(modeIndex, modeData);
        session.SendPacket(uniqueId, PacketUpdateMode, payload);
        session.ExpectAckOk(PacketUpdateMode);
    }

    public static void ConfigureZone(
        string host,
        int port,
        int timeoutMs,
        int deviceIndex,
        string? preferredDeviceName,
        int zoneIndex,
        OpenRgbZoneConfig zone)
    {
        using var session = Session.Connect(host, port, timeoutMs, "UnifiedRgb-ConfigureZone");
        if (session.Negotiated < 5)
        {
            throw new InvalidOperationException(
                $"OpenRGB SDK protocol {session.Negotiated} cannot ConfigureZone with size flags " +
                "(need protocol 5+ / OpenRGB 1.0). ResizeZone is a no-op for CM Gen2.");
        }

        var uniqueId = session.ResolveUniqueId(deviceIndex, preferredDeviceName);
        var payload = OpenRgbConfigureZoneClient.BuildConfigureZonePayload(zoneIndex, zone, session.Negotiated);
        session.SendPacket(uniqueId, PacketConfigureZone, payload);
        if (session.Negotiated >= 6)
            session.ExpectAckOk(PacketConfigureZone);
    }

    public static byte[] BuildUpdateLedsPayload(ReadOnlySpan<(byte R, byte G, byte B)> colors)
    {
        // data_size MUST equal the entire payload length, including data_size itself.
        var payloadLen = 4 + 2 + (4 * colors.Length);
        var payload = new byte[payloadLen];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), (uint)payloadLen);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), (ushort)colors.Length);
        var offset = 6;
        for (var i = 0; i < colors.Length; i++)
        {
            WriteRgbColor(payload.AsSpan(offset, 4), colors[i]);
            offset += 4;
        }

        return payload;
    }

    public static byte[] BuildUpdateZoneLedsPayload(int zoneIndex, ReadOnlySpan<(byte R, byte G, byte B)> colors)
    {
        var payloadLen = 4 + 4 + 2 + (4 * colors.Length);
        var payload = new byte[payloadLen];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), (uint)payloadLen);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), zoneIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8, 2), (ushort)colors.Length);
        var offset = 10;
        for (var i = 0; i < colors.Length; i++)
        {
            WriteRgbColor(payload.AsSpan(offset, 4), colors[i]);
            offset += 4;
        }

        return payload;
    }

    public static byte[] BuildUpdateModePayload(int modeIndex, byte[] modeData)
    {
        var payloadLen = 4 + 4 + modeData.Length;
        var payload = new byte[payloadLen];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), (uint)payloadLen);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), modeIndex);
        Buffer.BlockCopy(modeData, 0, payload, 8, modeData.Length);
        return payload;
    }

    private static void WriteRgbColor(Span<byte> dest, (byte R, byte G, byte B) color)
    {
        // OpenRGB RGBColor: low byte R, then G, B, pad (little-endian uint32).
        dest[0] = color.R;
        dest[1] = color.G;
        dest[2] = color.B;
        dest[3] = 0;
    }

    /// <summary>
    /// Short-lived protocol-6 SDK session: handshake, unique-ID map, send/ACK.
    /// </summary>
    private sealed class Session : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly int _timeoutMs;

        public uint Negotiated { get; }

        private Session(TcpClient tcp, NetworkStream stream, int timeoutMs, uint negotiated)
        {
            _tcp = tcp;
            _stream = stream;
            _timeoutMs = timeoutMs;
            Negotiated = negotiated;
        }

        public static Session Connect(string host, int port, int timeoutMs, string clientName)
        {
            if (timeoutMs <= 0)
                timeoutMs = 2000;

            var tcp = new TcpClient { NoDelay = true, ReceiveTimeout = timeoutMs, SendTimeout = timeoutMs };
            try
            {
                ConnectTcp(tcp, host, port, timeoutMs);
                var stream = tcp.GetStream();
                stream.ReadTimeout = timeoutMs;
                stream.WriteTimeout = timeoutMs;

                SendPacketStatic(stream, deviceId: 0, PacketSetClientName, EncodeCString(clientName));
                SendPacketStatic(stream, deviceId: 0, PacketRequestProtocolVersion,
                    BitConverter.GetBytes(ClientProtocolVersion));

                var negotiated = WaitForProtocolVersion(stream, timeoutMs);
                // Server also sends SET_SERVER_NAME (+ ACK on protocol 6). Drain those.
                DrainOptional(stream, TimeSpan.FromMilliseconds(Math.Min(400, timeoutMs)));

                return new Session(tcp, stream, timeoutMs, negotiated);
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Map OpenRGB.NET ordinal index → protocol-6 unique controller ID.
        /// Prefer matching <paramref name="preferredDeviceName"/> against
        /// REQUEST_CONTROLLER_DATA names (same order as the unique-ID list);
        /// fall back to list[ordinal] when names are unavailable or unmatched.
        /// </summary>
        public uint ResolveUniqueId(int ordinalIndex, string? preferredDeviceName)
        {
            if (Negotiated < 6)
            {
                if (ordinalIndex < 0)
                    throw new ArgumentOutOfRangeException(nameof(ordinalIndex));
                return (uint)ordinalIndex;
            }

            SendPacket(0, PacketRequestControllerCount, Array.Empty<byte>());
            var countPayload = WaitForPacket(PacketRequestControllerCount);
            var uniqueIds = ParseUniqueIds(countPayload);

            if (ordinalIndex < 0 || ordinalIndex >= uniqueIds.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(ordinalIndex),
                    $"Device {ordinalIndex} is out of range (server has {uniqueIds.Length} controller(s)).");
            }

            if (!string.IsNullOrWhiteSpace(preferredDeviceName))
            {
                for (var i = 0; i < uniqueIds.Length; i++)
                {
                    try
                    {
                        var name = RequestControllerName(uniqueIds[i]);
                        if (NamesMatch(name, preferredDeviceName))
                            return uniqueIds[i];
                    }
                    catch
                    {
                        // Fall through to ordinal mapping if a DATA request fails.
                    }
                }
            }

            // Same order as OpenRGB.NET GetAllControllerData / GetControllerCount indices.
            return uniqueIds[ordinalIndex];
        }

        private string RequestControllerName(uint uniqueId)
        {
            // Protocol 1+: request includes negotiated protocol version.
            var req = BitConverter.GetBytes(Negotiated);
            SendPacket(uniqueId, PacketRequestControllerData, req);
            var payload = WaitForPacket(PacketRequestControllerData);
            return ParseControllerName(payload);
        }

        public void SendPacket(uint deviceId, uint packetId, byte[] payload) =>
            SendPacketStatic(_stream, deviceId, packetId, payload);

        public void ExpectAckOk(uint forPacketId)
        {
            if (Negotiated < 6)
                return;

            var status = WaitForAck(forPacketId);
            if (status != AckOk)
            {
                throw new InvalidOperationException(
                    $"OpenRGB packet {forPacketId} ACK status {status} " +
                    "(5=INVALID_DATA usually means data_size did not equal the full packet payload; " +
                    "4=INVALID_ID means wrong unique controller ID).");
            }
        }

        private byte[] WaitForPacket(uint expectedId)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(_timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var remaining = (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds);
                var pkt = ReadPacket(_stream, remaining);
                if (pkt.Id == expectedId)
                    return pkt.Payload;
            }

            throw new TimeoutException($"Timed out waiting for OpenRGB packet {expectedId}.");
        }

        private uint WaitForAck(uint forPacketId)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(_timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var remaining = (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds);
                var pkt = ReadPacket(_stream, remaining);
                if (pkt.Id != PacketAck || pkt.Payload.Length < 8)
                    continue;
                var acked = BinaryPrimitives.ReadUInt32LittleEndian(pkt.Payload.AsSpan(0, 4));
                if (acked != forPacketId)
                    continue;
                return BinaryPrimitives.ReadUInt32LittleEndian(pkt.Payload.AsSpan(4, 4));
            }

            throw new TimeoutException($"Timed out waiting for OpenRGB ACK for packet {forPacketId}.");
        }

        public void Dispose()
        {
            try { _stream.Dispose(); } catch { /* ignore */ }
            try { _tcp.Dispose(); } catch { /* ignore */ }
        }
    }

    internal static uint[] ParseUniqueIds(byte[] countPayload)
    {
        if (countPayload.Length < 4)
            throw new InvalidOperationException("Controller-count reply was empty.");

        var count = BinaryPrimitives.ReadUInt32LittleEndian(countPayload.AsSpan(0, 4));
        var ids = new uint[count];
        var idsBytes = countPayload.Length - 4;
        if (idsBytes >= 4 * (int)count)
        {
            for (var i = 0; i < (int)count; i++)
            {
                var offset = 4 + (4 * i);
                ids[i] = BinaryPrimitives.ReadUInt32LittleEndian(countPayload.AsSpan(offset, 4));
            }
        }
        else
        {
            // Protocol <6 style: count only — treat ordinals as IDs.
            for (var i = 0; i < (int)count; i++)
                ids[i] = (uint)i;
        }

        return ids;
    }

    internal static string ParseControllerName(byte[] controllerDataPayload)
    {
        // data_size (4) + type (4) + name_len (2) + name[name_len]
        if (controllerDataPayload.Length < 10)
            throw new InvalidOperationException("Controller data reply too short for name.");

        var offset = 4; // skip data_size
        offset += 4;    // skip type
        var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(controllerDataPayload.AsSpan(offset, 2));
        offset += 2;
        if (nameLen == 0 || offset + nameLen > controllerDataPayload.Length)
            throw new InvalidOperationException("Controller data name length is invalid.");

        var raw = Encoding.UTF8.GetString(controllerDataPayload, offset, nameLen);
        return raw.TrimEnd('\0').Trim();
    }

    private static bool NamesMatch(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static void ConnectTcp(TcpClient tcp, string host, int port, int timeoutMs)
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

    private static void SendPacketStatic(NetworkStream stream, uint deviceId, uint packetId, byte[] payload)
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
