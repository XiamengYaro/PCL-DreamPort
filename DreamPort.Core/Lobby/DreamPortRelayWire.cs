using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DreamPort.Core.Lobby;

/// <summary>
/// 梦港联机中继控制帧编解码（与 DreamPort-Relay <c>internal/wire/frame.go</c> 逐字节对齐）：
/// 4 字节大端无符号整数长度前缀 + UTF-8 JSON <c>{"type":"...","body":...}</c>。
/// </summary>
public static class DreamPortRelayWire
{
    public const int ProtocolVersion = 1;
    public const int MaxFrameSize = 64 * 1024;

    public const string TypeHello = "hello";
    public const string TypeHelloAck = "hello_ack";
    public const string TypeHelloReject = "hello_reject";
    public const string TypePeerAdded = "peer_added";
    public const string TypePeerRemoved = "peer_removed";
    public const string TypePeerDataRequest = "peer_data_request";
    public const string TypeKicked = "kicked";
    public const string TypeMemberList = "member_list";
    public const string TypeMemberListRequest = "member_list_request";
    public const string TypeKickMember = "kick_member";
    public const string TypeKickResult = "kick_result";
    public const string TypeKickList = "kick_list";
    public const string TypeUnkickMember = "unkick_member";
    public const string TypeRoomClosed = "room_closed";
    public const string TypeHostGone = "host_gone";
    public const string TypeHostBack = "host_back";
    public const string TypeHostStatus = "host_status";
    public const string TypePing = "ping";
    public const string TypePong = "pong";

    public const string RoleHostControl = "host-control";
    public const string RoleGuestControl = "guest-control";
    public const string RoleHostData = "host-data";
    public const string RoleGuestData = "guest-data";

    public const string ReasonRoomNotFound = "ROOM_NOT_FOUND";
    public const string ReasonRoomFull = "ROOM_FULL";
    public const string ReasonRoomPrivate = "ROOM_PRIVATE";
    public const string ReasonTicketInvalid = "TICKET_INVALID";
    public const string ReasonTicketExpired = "TICKET_EXPIRED";
    public const string ReasonBanned = "BANNED";
    public const string ReasonSiteFull = "SITE_FULL";
    public const string ReasonRelayMaintenance = "RELAY_MAINTENANCE";
    public const string ReasonUnsupportedVersion = "UNSUPPORTED_VERSION";
    public const string ReasonNotMinecraft = "NOT_MINECRAFT";
    public const string ReasonTooManyConnections = "TOO_MANY_CONNECTIONS";
    public const string ReasonRateLimited = "RATE_LIMITED";
    public const string ReasonIpBanned = "IP_BANNED";
    public const string ReasonPeerGone = "PEER_GONE";
    public const string ReasonHostKicked = "HOST_KICKED";
    public const string ReasonRoomClosed = "ROOM_CLOSED";
    public const string ReasonHostGone = "HOST_GONE";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string RejectMessage(string? reason, string? fallback = null)
    {
        var key = reason?.Trim().ToUpperInvariant() ?? string.Empty;
        return key switch
        {
            ReasonRoomNotFound => "房间不存在或已关闭",
            ReasonRoomFull => "房间人数已满",
            ReasonRoomPrivate => "该房间为仅限好友加入，需要房间码",
            ReasonTicketInvalid => "房票校验失败，请重新登录梦港账号后再试",
            ReasonTicketExpired => "房票已过期，请刷新大厅或重新发起联机",
            ReasonBanned => "你的账号已被限制联机",
            ReasonSiteFull => "当前中继在线总人数已满，请稍后再试",
            ReasonRelayMaintenance => "梦港联机服务正在维护，请稍后再试",
            ReasonUnsupportedVersion => "客户端与联机服务协议版本不兼容，请更新启动器",
            ReasonNotMinecraft => "连接流量不符合 Minecraft 协议特征，已被中继拒绝",
            ReasonTooManyConnections => "当前并发连接数过多，请稍后再试",
            ReasonRateLimited => "操作过于频繁，请稍后再试",
            ReasonIpBanned => "当前网络已被中继临时限制，请稍后再试",
            ReasonPeerGone => "对端连接已断开",
            // 房主操作与全站封禁必须严格区分文案，避免玩家误以为账号被封
            ReasonHostKicked => "房主把你移出了这个房间",
            ReasonRoomClosed => "房主已结束联机",
            ReasonHostGone => "房主暂时不在游戏中",
            _ => !string.IsNullOrWhiteSpace(fallback) ? fallback.Trim() : "联机连接失败，请稍后再试"
        };
    }

    public static byte[] EncodeFrame(string msgType, object? body = null)
    {
        if (string.IsNullOrWhiteSpace(msgType))
            throw new ArgumentException("控制帧类型不能为空", nameof(msgType));

        byte[] payload;
        if (body is null)
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(new { type = msgType }, JsonOptions);
        }
        else
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(new { type = msgType, body }, JsonOptions);
        }

        if (payload.Length == 0 || payload.Length > MaxFrameSize)
            throw new InvalidOperationException($"控制帧长度超出允许范围（{payload.Length} 字节）");

        var packet = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(0, 4), (uint)payload.Length);
        Buffer.BlockCopy(payload, 0, packet, 4, payload.Length);
        return packet;
    }

    public static async Task WriteFrameAsync(
        Stream stream,
        string msgType,
        object? body = null,
        CancellationToken ct = default)
    {
        var packet = EncodeFrame(msgType, body);
        await stream.WriteAsync(packet, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<RelayFrame?> ReadFrameAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[4];
        var headerRead = await ReadAtMostAsync(stream, header, ct).ConfigureAwait(false);
        if (headerRead == 0)
            return null;
        if (headerRead < 4)
            throw new EndOfStreamException("控制帧长度头不完整，连接已断开");

        var len = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (len == 0 || len > MaxFrameSize)
            throw new InvalidDataException($"非法控制帧长度：{len}");

        var payload = new byte[len];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"控制帧 JSON 格式无效：{ex.Message}", ex);
        }

        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("type", out var typeEl)
            || typeEl.ValueKind != JsonValueKind.String)
        {
            doc.Dispose();
            throw new InvalidDataException("控制帧缺少合法的 type 字段");
        }

        var type = typeEl.GetString() ?? string.Empty;
        var body = doc.RootElement.TryGetProperty("body", out var bodyEl) ? bodyEl : default;
        return new RelayFrame(type, doc, body);
    }

    private static async Task<int> ReadAtMostAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false);
            if (read == 0)
                return total;
            total += read;
        }

        return total;
    }

    public sealed class RelayFrame(string type, JsonDocument document, JsonElement body) : IDisposable
    {
        public string Type { get; } = type;
        public JsonDocument Document { get; } = document;
        public JsonElement Body { get; } = body;

        public void Dispose() => Document.Dispose();
    }
}
