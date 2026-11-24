using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using PCL.Core.Logging;

namespace DreamPort.Core.Lobby;

/// <summary>
/// 梦港联机 TCP/TLS 中继隧道管理器（零外部二进制依赖）：
/// - 控制连接走 4 字节大端长度 + JSON 控制帧；
/// - 数据连接发完首帧 <c>hello</c> 后立即切换双向裸字节转发（不加任何帧头，也不合成任何 Minecraft 握手包）；
/// - 同一时刻仅允许处于一个活跃会话（房主开房或访客加入）。
/// </summary>
public sealed class DreamPortLobbyTunnel : IAsyncDisposable
{
    private readonly Lock _stateLock = new();
    private DreamPortHostSession? _hostSession;
    private DreamPortGuestSession? _guestSession;

    public bool IsBusy
    {
        get
        {
            lock (_stateLock)
            {
                return (_hostSession is { IsStopped: false }) || (_guestSession is { IsStopped: false });
            }
        }
    }

    public DreamPortHostSession? CurrentHostSession
    {
        get
        {
            lock (_stateLock)
            {
                return _hostSession is { IsStopped: false } ? _hostSession : null;
            }
        }
    }

    public DreamPortGuestSession? CurrentGuestSession
    {
        get
        {
            lock (_stateLock)
            {
                return _guestSession is { IsStopped: false } ? _guestSession : null;
            }
        }
    }

    public async Task<DreamPortHostSession> StartHostAsync(
        DreamPortRelayEndpoint endpoint,
        string roomCode,
        string ticket,
        string playerName,
        int localGamePort,
        Action<DreamPortLobbyTunnelEvent>? onEvent = null,
        Action<IReadOnlyList<DreamPortLobbyMember>>? onMembersChanged = null,
        Action<bool>? onGameAliveChanged = null,
        CancellationToken ct = default)
    {
        ValidateEndpoint(endpoint);
        if (localGamePort is < 1 or > 65535)
            throw new ArgumentException("本地游戏端口必须是 1-65535 的整数。", nameof(localGamePort));

        lock (_stateLock)
        {
            if (IsBusyInternal())
                throw new InvalidOperationException("当前已在联机房间中，请先结束当前联机。");
        }

        if (!await DreamPortLanService.PingLocalPortAsync(localGamePort, 1500, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"本机 {localGamePort} 端口没有可连接的游戏服务，请先在 Minecraft 中点击「对局域网开放」，再检测或填写正确端口。");
        }

        var control = await RelayStreamConnection.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        try
        {
            await control.SendFrameAsync(DreamPortRelayWire.TypeHello, new
            {
                protocol = DreamPortRelayWire.ProtocolVersion,
                role = DreamPortRelayWire.RoleHostControl,
                room = roomCode,
                ticket,
                playerName
            }, ct).ConfigureAwait(false);

            await AwaitHelloAckAsync(control, ct).ConfigureAwait(false);
        }
        catch
        {
            await control.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var session = new DreamPortHostSession(
            endpoint,
            roomCode,
            ticket,
            localGamePort,
            control,
            onEvent,
            onMembersChanged,
            onGameAliveChanged,
            ClearHostSessionIfMatch);

        lock (_stateLock)
        {
            if (IsBusyInternal())
            {
                _ = session.DisposeAsync();
                throw new InvalidOperationException("当前已在联机房间中，请先结束当前联机。");
            }
            _hostSession = session;
        }

        session.StartBackgroundLoops();
        return session;
    }

    public async Task<DreamPortGuestSession> StartGuestAsync(
        DreamPortRelayEndpoint endpoint,
        string roomCode,
        string ticket,
        string playerName,
        int preferLocalPort = 0,
        string? advertiseName = null,
        Action<DreamPortLobbyTunnelEvent>? onEvent = null,
        CancellationToken ct = default)
    {
        ValidateEndpoint(endpoint);

        lock (_stateLock)
        {
            if (IsBusyInternal())
                throw new InvalidOperationException("当前已在联机房间中，请先离开当前房间。");
        }

        var control = await RelayStreamConnection.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        string peerId;
        try
        {
            await control.SendFrameAsync(DreamPortRelayWire.TypeHello, new
            {
                protocol = DreamPortRelayWire.ProtocolVersion,
                role = DreamPortRelayWire.RoleGuestControl,
                room = roomCode,
                ticket,
                playerName
            }, ct).ConfigureAwait(false);

            peerId = await AwaitHelloAckAsync(control, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(peerId))
                throw new InvalidOperationException("中继未返回有效的访客标识（peerId）。");
        }
        catch
        {
            await control.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        TcpListener? listener = null;
        try
        {
            listener = ListenLocalWithFallback(preferLocalPort);
        }
        catch
        {
            await control.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var localPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var session = new DreamPortGuestSession(
            endpoint,
            roomCode,
            ticket,
            peerId,
            localPort,
            advertiseName ?? "梦港联机房间",
            control,
            listener,
            onEvent,
            ClearGuestSessionIfMatch);

        lock (_stateLock)
        {
            if (IsBusyInternal())
            {
                _ = session.DisposeAsync();
                throw new InvalidOperationException("当前已在联机房间中，请先离开当前房间。");
            }
            _guestSession = session;
        }

        session.StartBackgroundLoops();
        return session;
    }

    public async Task StopAllAsync()
    {
        DreamPortHostSession? host;
        DreamPortGuestSession? guest;
        lock (_stateLock)
        {
            host = _hostSession;
            guest = _guestSession;
            _hostSession = null;
            _guestSession = null;
        }

        if (host is not null)
            await host.StopAsync().ConfigureAwait(false);
        if (guest is not null)
            await guest.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync().ConfigureAwait(false);
    }

    private bool IsBusyInternal()
        => (_hostSession is { IsStopped: false }) || (_guestSession is { IsStopped: false });

    private void ClearHostSessionIfMatch(DreamPortHostSession session)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(_hostSession, session))
                _hostSession = null;
        }
    }

    private void ClearGuestSessionIfMatch(DreamPortGuestSession session)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(_guestSession, session))
                _guestSession = null;
        }
    }

    private static void ValidateEndpoint(DreamPortRelayEndpoint endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Host))
            throw new ArgumentException("中继主机地址不能为空。", nameof(endpoint));
        if (endpoint.Port is < 1 or > 65535)
            throw new ArgumentException("中继端口必须在 1-65535 之间。", nameof(endpoint));
    }

    private static async Task<string> AwaitHelloAckAsync(RelayStreamConnection conn, CancellationToken ct)
    {
        using var ackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ackCts.CancelAfter(TimeSpan.FromSeconds(15));
        using var frame = await conn.ReadFrameAsync(ackCts.Token).ConfigureAwait(false)
            ?? throw new EndOfStreamException("中继在握手完成前关闭了连接。");

        if (string.Equals(frame.Type, DreamPortRelayWire.TypeHelloAck, StringComparison.Ordinal))
        {
            if (frame.Body.ValueKind == JsonValueKind.Object
                && frame.Body.TryGetProperty("peerId", out var peerProp)
                && peerProp.ValueKind == JsonValueKind.String)
            {
                return peerProp.GetString() ?? string.Empty;
            }

            return string.Empty;
        }

        if (string.Equals(frame.Type, DreamPortRelayWire.TypeHelloReject, StringComparison.Ordinal))
        {
            var reason = GetJsonString(frame.Body, "reason") ?? string.Empty;
            var msg = GetJsonString(frame.Body, "message");
            throw new DreamPortRelayRejectedException(reason, DreamPortRelayWire.RejectMessage(reason, msg));
        }

        throw new InvalidDataException($"中继返回非预期握手帧：{frame.Type}");
    }

    private static TcpListener ListenLocalWithFallback(int preferPort)
    {
        var port = preferPort is >= 1 and <= 65535 ? preferPort : 0;
        var listener = new TcpListener(IPAddress.Any, port);
        try
        {
            listener.Start();
            return listener;
        }
        catch (SocketException) when (port != 0)
        {
            listener.Dispose();
            var fallback = new TcpListener(IPAddress.Any, 0);
            fallback.Start();
            return fallback;
        }
    }

    internal static string? GetJsonString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object
            || !el.TryGetProperty(name, out var prop)
            || prop.ValueKind == JsonValueKind.Null
            || prop.ValueKind == JsonValueKind.Undefined)
            return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
    }

    internal static bool GetJsonBool(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var prop))
            return false;
        return prop.ValueKind == JsonValueKind.True;
    }

    internal static long GetJsonLong(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var prop))
            return 0;
        return prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var val) ? val : 0;
    }

    internal sealed class RelayStreamConnection : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly Stream _stream;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private int _disposed;

        private RelayStreamConnection(TcpClient tcp, Stream stream)
        {
            _tcp = tcp;
            _stream = stream;
        }

        public Stream Stream => _stream;

        public static async Task<RelayStreamConnection> ConnectAsync(DreamPortRelayEndpoint endpoint, CancellationToken ct)
        {
            var tcp = new TcpClient { NoDelay = true };
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await tcp.ConnectAsync(endpoint.Host, endpoint.Port, connectCts.Token).ConfigureAwait(false);
                if (!endpoint.Tls)
                    return new RelayStreamConnection(tcp, tcp.GetStream());

                var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
                var authOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = endpoint.Host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                };
                await ssl.AuthenticateAsClientAsync(authOptions, connectCts.Token).ConfigureAwait(false);
                return new RelayStreamConnection(tcp, ssl);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                tcp.Dispose();
                throw new TimeoutException($"连接中继服务 {endpoint.Host}:{endpoint.Port} 超时（15 秒）。");
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }

        public async Task SendFrameAsync(string type, object? body = null, CancellationToken ct = default)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return;
                await DreamPortRelayWire.WriteFrameAsync(_stream, type, body, ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public Task<DreamPortRelayWire.RelayFrame?> ReadFrameAsync(CancellationToken ct)
            => DreamPortRelayWire.ReadFrameAsync(_stream, ct);

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return ValueTask.CompletedTask;

            try { _stream.Dispose(); } catch { /* ignore */ }
            try { _tcp.Dispose(); } catch { /* ignore */ }
            return ValueTask.CompletedTask;
        }
    }

    internal static async Task PumpBidirectionalAsync(
        Stream relayStream,
        Stream gameStream,
        Action<long> addBytesIn,
        Action<long> addBytesOut,
        Action? onFirstRelayBytes,
        CancellationToken ct)
    {
        using var pairCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = pairCts.Token;

        var firstMarked = 0;
        var relayToGame = CopyDirectionAsync(
            relayStream,
            gameStream,
            count =>
            {
                addBytesIn(count);
                if (onFirstRelayBytes is not null && Interlocked.Exchange(ref firstMarked, 1) == 0)
                    onFirstRelayBytes();
            },
            pairCts,
            token);

        var gameToRelay = CopyDirectionAsync(
            gameStream,
            relayStream,
            addBytesOut,
            pairCts,
            token);

        await Task.WhenAll(relayToGame, gameToRelay).ConfigureAwait(false);
    }

    private static async Task CopyDirectionAsync(
        Stream source,
        Stream destination,
        Action<long> onBytesCopied,
        CancellationTokenSource pairCts,
        CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read == 0)
                    break;

                using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                writeCts.CancelAfter(TimeSpan.FromSeconds(30));
                await destination.WriteAsync(buffer.AsMemory(0, read), writeCts.Token).ConfigureAwait(false);
                await destination.FlushAsync(writeCts.Token).ConfigureAwait(false);
                onBytesCopied(read);
            }
        }
        catch
        {
            // 对端关闭或读写超时，结束双向泵
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            try { pairCts.Cancel(); } catch { /* ignore */ }
        }
    }
}

/// <summary>房主侧联机中继控制面 + 数据面会话。</summary>
public sealed class DreamPortHostSession : IAsyncDisposable
{
    private readonly DreamPortRelayEndpoint _endpoint;
    private readonly string _roomCode;
    private readonly string _ticket;
    private readonly int _localGamePort;
    private readonly DreamPortLobbyTunnel.RelayStreamConnection _control;
    private readonly Action<DreamPortLobbyTunnelEvent>? _onEvent;
    private readonly Action<IReadOnlyList<DreamPortLobbyMember>>? _onMembersChanged;
    private readonly Action<bool>? _onGameAliveChanged;
    private readonly Action<DreamPortHostSession> _releaseCallback;
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _stopToken;
    private readonly ConcurrentDictionary<string, HostDataConnection> _pendingByPeer = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _retiredPeers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _peerNames = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _nameToPeer = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<HostDataConnection, byte> _allDataConnections = new();
    private readonly ConcurrentDictionary<string, Task> _dialingByPeer = new(StringComparer.Ordinal);
    private readonly Lock _membersLock = new();

    private IReadOnlyList<DreamPortLobbyMember> _members = [];
    private long _bytesIn;
    private long _bytesOut;
    private int _stopped;

    internal DreamPortHostSession(
        DreamPortRelayEndpoint endpoint,
        string roomCode,
        string ticket,
        int localGamePort,
        DreamPortLobbyTunnel.RelayStreamConnection control,
        Action<DreamPortLobbyTunnelEvent>? onEvent,
        Action<IReadOnlyList<DreamPortLobbyMember>>? onMembersChanged,
        Action<bool>? onGameAliveChanged,
        Action<DreamPortHostSession> releaseCallback)
    {
        _endpoint = endpoint;
        _roomCode = roomCode;
        _ticket = ticket;
        _localGamePort = localGamePort;
        _control = control;
        _onEvent = onEvent;
        _onMembersChanged = onMembersChanged;
        _onGameAliveChanged = onGameAliveChanged;
        _releaseCallback = releaseCallback;
        _stopToken = _cts.Token;
    }

    public string RoomCode => _roomCode;
    public int LocalGamePort => _localGamePort;
    public bool IsStopped => Volatile.Read(ref _stopped) != 0;

    internal void StartBackgroundLoops()
    {
        _ = RunReadControlLoopAsync();
        _ = RunHeartbeatLoopAsync();
        _ = RunHostStatusLoopAsync();
    }

    public IReadOnlyList<DreamPortLobbyMember> GetMembers()
    {
        lock (_membersLock)
            return _members;
    }

    public DreamPortLobbyTunnelStats GetStats()
        => new(_allDataConnections.Count, Interlocked.Read(ref _bytesIn), Interlocked.Read(ref _bytesOut));

    public Task RefreshMembersAsync(CancellationToken ct = default)
        => IsStopped
            ? Task.CompletedTask
            : _control.SendFrameAsync(DreamPortRelayWire.TypeMemberListRequest, null, ct);

    public Task KickMemberAsync(string playerName, string? reason = null, CancellationToken ct = default)
    {
        if (IsStopped || string.IsNullOrWhiteSpace(playerName))
            return Task.CompletedTask;
        return _control.SendFrameAsync(DreamPortRelayWire.TypeKickMember, new { playerName = playerName.Trim(), reason }, ct);
    }

    public Task UnkickMemberAsync(string playerName, CancellationToken ct = default)
    {
        if (IsStopped || string.IsNullOrWhiteSpace(playerName))
            return Task.CompletedTask;
        return _control.SendFrameAsync(DreamPortRelayWire.TypeUnkickMember, new { playerName = playerName.Trim() }, ct);
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        _releaseCallback(this);
        try { _cts.Cancel(); } catch { /* ignore */ }

        foreach (var conn in _allDataConnections.Keys)
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }

        _allDataConnections.Clear();
        _pendingByPeer.Clear();
        await _control.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunReadControlLoopAsync()
    {
        try
        {
            while (!_stopToken.IsCancellationRequested)
            {
                using var frame = await _control.ReadFrameAsync(_stopToken).ConfigureAwait(false);
                if (frame is null)
                {
                    EmitEvent("closed", "与联机中继服务的连接已断开");
                    break;
                }

                await HandleControlFrameAsync(frame.Type, frame.Body).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // normal stop
        }
        catch (Exception ex)
        {
            if (!IsStopped)
            {
                LogWrapper.Debug(ex, "DreamPort", "房主控制连接读取异常");
                EmitEvent("closed", $"与联机服务的连接中断：{ex.Message}");
            }
        }
        finally
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleControlFrameAsync(string type, JsonElement body)
    {
        switch (type)
        {
            case DreamPortRelayWire.TypePing:
                await _control.SendFrameAsync(DreamPortRelayWire.TypePong, null, _stopToken).ConfigureAwait(false);
                break;

            case DreamPortRelayWire.TypePeerAdded:
            {
                var pid = DreamPortLobbyTunnel.GetJsonString(body, "peerId") ?? string.Empty;
                var playerName = DreamPortLobbyTunnel.GetJsonString(body, "playerName") ?? string.Empty;
                if (!string.IsNullOrEmpty(pid))
                {
                    await RememberPeerAsync(pid, playerName).ConfigureAwait(false);
                    _ = OpenRelayDataConnAsync(pid);
                }
                break;
            }

            case DreamPortRelayWire.TypePeerDataRequest:
            {
                var pid = DreamPortLobbyTunnel.GetJsonString(body, "peerId") ?? string.Empty;
                if (!string.IsNullOrEmpty(pid) && !_retiredPeers.ContainsKey(pid))
                    _ = OpenRelayDataConnAsync(pid);
                break;
            }

            case DreamPortRelayWire.TypePeerRemoved:
            {
                var pid = DreamPortLobbyTunnel.GetJsonString(body, "peerId") ?? string.Empty;
                if (!string.IsNullOrEmpty(pid))
                {
                    await ClosePeerAsync(pid).ConfigureAwait(false);
                    EmitEvent("peer-left", "一位联机成员离开了房间", pid);
                }
                break;
            }

            case DreamPortRelayWire.TypeMemberList:
            {
                ApplyMembers(body);
                await ReconcileMembersAsync().ConfigureAwait(false);
                break;
            }

            case DreamPortRelayWire.TypeKickResult:
            {
                var ok = body.ValueKind == JsonValueKind.Object
                    && body.TryGetProperty("ok", out var okEl)
                    && okEl.ValueKind == JsonValueKind.True;
                var name = DreamPortLobbyTunnel.GetJsonString(body, "playerName") ?? string.Empty;
                if (!ok)
                {
                    var errMsg = DreamPortLobbyTunnel.GetJsonString(body, "message") ?? "未知原因";
                    EmitEvent("error", $"踢出成员失败：{errMsg}");
                }
                else
                {
                    EmitEvent("member-kicked", $"已将 {name} 移出房间");
                }
                break;
            }

            case DreamPortRelayWire.TypeKicked:
            case DreamPortRelayWire.TypeRoomClosed:
            {
                var reason = DreamPortLobbyTunnel.GetJsonString(body, "reason");
                var msg = DreamPortLobbyTunnel.GetJsonString(body, "message");
                EmitEvent(
                    type == DreamPortRelayWire.TypeKicked ? "kicked" : "room-closed",
                    DreamPortRelayWire.RejectMessage(reason, msg));
                await StopAsync().ConfigureAwait(false);
                break;
            }
        }
    }

    private async Task RememberPeerAsync(string peerId, string playerName)
    {
        _retiredPeers.TryRemove(peerId, out _);
        if (!string.IsNullOrWhiteSpace(playerName))
        {
            if (_nameToPeer.TryGetValue(playerName, out var oldPeer) && !string.Equals(oldPeer, peerId, StringComparison.Ordinal))
            {
                await ClosePeerAsync(oldPeer).ConfigureAwait(false);
            }

            _peerNames[peerId] = playerName;
            _nameToPeer[playerName] = peerId;
        }
    }

    private async Task ClosePeerAsync(string peerId)
    {
        _retiredPeers[peerId] = 0;
        _pendingByPeer.TryRemove(peerId, out _);

        foreach (var conn in _allDataConnections.Keys)
        {
            if (string.Equals(conn.PeerId, peerId, StringComparison.Ordinal))
                await conn.DisposeAsync().ConfigureAwait(false);
        }

        if (_peerNames.TryRemove(peerId, out var name)
            && _nameToPeer.TryGetValue(name, out var mapped)
            && string.Equals(mapped, peerId, StringComparison.Ordinal))
        {
            _nameToPeer.TryRemove(name, out _);
        }
    }

    private void ApplyMembers(JsonElement body)
    {
        var list = new List<DreamPortLobbyMember>();
        if (body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("members", out var membersEl)
            && membersEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in membersEl.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object)
                    continue;
                list.Add(new DreamPortLobbyMember(
                    PeerId: DreamPortLobbyTunnel.GetJsonString(m, "peerId") ?? string.Empty,
                    PlayerName: DreamPortLobbyTunnel.GetJsonString(m, "playerName") ?? string.Empty,
                    IsHost: DreamPortLobbyTunnel.GetJsonBool(m, "isHost"),
                    Kicked: DreamPortLobbyTunnel.GetJsonBool(m, "kicked"),
                    JoinedAtMs: DreamPortLobbyTunnel.GetJsonLong(m, "joinedAt")));
            }
        }

        lock (_membersLock)
            _members = list;

        try { _onMembersChanged?.Invoke(list); } catch { /* ignore */ }
        EmitEvent("members", $"房间成员名单已更新（当前 {list.Count} 项）");
    }

    private async Task ReconcileMembersAsync()
    {
        HashSet<string> activeGuestPeers;
        lock (_membersLock)
        {
            activeGuestPeers = _members
                .Where(m => !m.IsHost && !string.IsNullOrEmpty(m.PeerId))
                .Select(m => m.PeerId)
                .ToHashSet(StringComparer.Ordinal);
        }

        var known = new HashSet<string>(_peerNames.Keys, StringComparer.Ordinal);
        foreach (var pendingPeer in _pendingByPeer.Keys)
            known.Add(pendingPeer);
        foreach (var conn in _allDataConnections.Keys)
            known.Add(conn.PeerId);

        foreach (var peerId in known)
        {
            if (!activeGuestPeers.Contains(peerId))
                await ClosePeerAsync(peerId).ConfigureAwait(false);
        }

        foreach (var retired in _retiredPeers.Keys)
        {
            if (!activeGuestPeers.Contains(retired))
                _retiredPeers.TryRemove(retired, out _);
        }
    }

    private Task OpenRelayDataConnAsync(string peerId)
    {
        if (IsStopped || string.IsNullOrEmpty(peerId) || _retiredPeers.ContainsKey(peerId))
            return Task.CompletedTask;

        if (_pendingByPeer.TryGetValue(peerId, out var pending) && !pending.IsDisposed)
            return Task.CompletedTask;

        var dialTask = _dialingByPeer.GetOrAdd(peerId, _ => DialRelayDataCoreAsync(peerId));
        return dialTask;
    }

    private async Task DialRelayDataCoreAsync(string peerId)
    {
        TcpClient? gameTcp = null;
        DreamPortLobbyTunnel.RelayStreamConnection? relayConn = null;
        try
        {
            if (IsStopped || _retiredPeers.ContainsKey(peerId))
                return;

            // ① 先连本机开放的游戏端口
            gameTcp = new TcpClient { NoDelay = true };
            using var gameCts = CancellationTokenSource.CreateLinkedTokenSource(_stopToken);
            gameCts.CancelAfter(TimeSpan.FromSeconds(5));
            await gameTcp.ConnectAsync(IPAddress.Loopback, _localGamePort, gameCts.Token).ConfigureAwait(false);

            if (IsStopped || _retiredPeers.ContainsKey(peerId))
            {
                gameTcp.Dispose();
                return;
            }

            // ② 建立中继数据连接
            relayConn = await DreamPortLobbyTunnel.RelayStreamConnection.ConnectAsync(_endpoint, _stopToken).ConfigureAwait(false);
            if (IsStopped || _retiredPeers.ContainsKey(peerId))
            {
                await relayConn.DisposeAsync().ConfigureAwait(false);
                gameTcp.Dispose();
                return;
            }

            var dataState = new HostDataConnection(peerId, gameTcp, relayConn, conn =>
            {
                _allDataConnections.TryRemove(conn, out _);
                _pendingByPeer.TryRemove(new KeyValuePair<string, HostDataConnection>(conn.PeerId, conn));
            });

            _allDataConnections[dataState] = 0;
            _pendingByPeer[peerId] = dataState;

            // ③ 发完首帧 hello 后即切换裸字节模式，不等待 hello_ack，也不发送任何合成握手
            await relayConn.SendFrameAsync(DreamPortRelayWire.TypeHello, new
            {
                protocol = DreamPortRelayWire.ProtocolVersion,
                role = DreamPortRelayWire.RoleHostData,
                room = _roomCode,
                ticket = _ticket,
                peerId
            }, _stopToken).ConfigureAwait(false);

            EmitEvent("peer-joined", "有联机成员建立游戏连接", peerId);

            // ④ 启动双向裸字节转发（中继会把访客首包原样回灌至此）
            _ = Task.Run(async () =>
            {
                try
                {
                    await DreamPortLobbyTunnel.PumpBidirectionalAsync(
                        relayConn.Stream,
                        gameTcp.GetStream(),
                        count => Interlocked.Add(ref _bytesIn, count),
                        count => Interlocked.Add(ref _bytesOut, count),
                        onFirstRelayBytes: () =>
                            _pendingByPeer.TryRemove(new KeyValuePair<string, HostDataConnection>(peerId, dataState)),
                        _stopToken).ConfigureAwait(false);
                }
                finally
                {
                    await dataState.DisposeAsync().ConfigureAwait(false);
                }
            });
        }
        catch (OperationCanceledException)
        {
            if (relayConn is not null)
                await relayConn.DisposeAsync().ConfigureAwait(false);
            gameTcp?.Dispose();
        }
        catch (Exception ex)
        {
            if (!IsStopped)
                EmitEvent("error", $"建立游戏数据转发连接失败（{_localGamePort}）：{ex.Message}", peerId);
            if (relayConn is not null)
                await relayConn.DisposeAsync().ConfigureAwait(false);
            gameTcp?.Dispose();
        }
        finally
        {
            _dialingByPeer.TryRemove(peerId, out _);
        }
    }

    private async Task RunHeartbeatLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(_stopToken).ConfigureAwait(false))
            {
                await _control.SendFrameAsync(DreamPortRelayWire.TypePing, null, _stopToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // control loop handles disconnection
        }
    }

    private async Task RunHostStatusLoopAsync()
    {
        await ReportHostStatusOnceAsync().ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(_stopToken).ConfigureAwait(false))
            {
                await ReportHostStatusOnceAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // normal stop
        }
    }

    private async Task ReportHostStatusOnceAsync()
    {
        if (IsStopped)
            return;

        var alive = await DreamPortLanService.PingLocalPortAsync(_localGamePort, 1500, _stopToken).ConfigureAwait(false);
        try { _onGameAliveChanged?.Invoke(alive); } catch { /* ignore */ }
        if (!IsStopped)
        {
            await _control.SendFrameAsync(DreamPortRelayWire.TypeHostStatus, new
            {
                alive,
                port = _localGamePort
            }, _stopToken).ConfigureAwait(false);
        }
    }

    private void EmitEvent(string kind, string message, string? peerId = null)
    {
        try { _onEvent?.Invoke(new DreamPortLobbyTunnelEvent(kind, message, peerId)); } catch { /* ignore */ }
    }

    private sealed class HostDataConnection(
        string peerId,
        TcpClient gameTcp,
        DreamPortLobbyTunnel.RelayStreamConnection relayConn,
        Action<HostDataConnection> onDisposed) : IAsyncDisposable
    {
        private int _disposed;

        public string PeerId { get; } = peerId;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            onDisposed(this);
            try { gameTcp.Dispose(); } catch { /* ignore */ }
            await relayConn.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>访客侧联机中继控制面 + 本地监听 + 伪装局域网广播会话。</summary>
public sealed class DreamPortGuestSession : IAsyncDisposable
{
    private readonly DreamPortRelayEndpoint _endpoint;
    private readonly string _roomCode;
    private readonly string _ticket;
    private readonly string _peerId;
    private readonly int _localPort;
    private readonly DreamPortLobbyTunnel.RelayStreamConnection _control;
    private readonly TcpListener _listener;
    private readonly DreamPortLanAdvertiser _advertiser;
    private readonly Action<DreamPortLobbyTunnelEvent>? _onEvent;
    private readonly Action<DreamPortGuestSession> _releaseCallback;
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _stopToken;
    private readonly ConcurrentDictionary<GuestDataConnection, byte> _dataConnections = new();
    private string? _kickedReason;
    private long _bytesIn;
    private long _bytesOut;
    private int _stopped;

    internal DreamPortGuestSession(
        DreamPortRelayEndpoint endpoint,
        string roomCode,
        string ticket,
        string peerId,
        int localPort,
        string advertiseName,
        DreamPortLobbyTunnel.RelayStreamConnection control,
        TcpListener listener,
        Action<DreamPortLobbyTunnelEvent>? onEvent,
        Action<DreamPortGuestSession> releaseCallback)
    {
        _endpoint = endpoint;
        _roomCode = roomCode;
        _ticket = ticket;
        _peerId = peerId;
        _localPort = localPort;
        _control = control;
        _listener = listener;
        _onEvent = onEvent;
        _releaseCallback = releaseCallback;
        _stopToken = _cts.Token;
        _advertiser = new DreamPortLanAdvertiser(
            advertiseName,
            localPort,
            warn => EmitEvent("warn", warn));
    }

    public string RoomCode => _roomCode;
    public string PeerId => _peerId;
    public int LocalPort => _localPort;
    public bool IsStopped => Volatile.Read(ref _stopped) != 0;

    internal void StartBackgroundLoops()
    {
        _ = RunAcceptLoopAsync();
        _ = RunReadControlLoopAsync();
        _ = RunHeartbeatLoopAsync();
    }

    public DreamPortLobbyTunnelStats GetStats()
        => new(_dataConnections.Count, Interlocked.Read(ref _bytesIn), Interlocked.Read(ref _bytesOut));

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        _releaseCallback(this);
        try { _cts.Cancel(); } catch { /* ignore */ }
        _advertiser.Dispose();
        try { _listener.Stop(); } catch { /* ignore */ }
        try { _listener.Dispose(); } catch { /* ignore */ }

        foreach (var conn in _dataConnections.Keys)
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }

        _dataConnections.Clear();
        await _control.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunAcceptLoopAsync()
    {
        try
        {
            while (!_stopToken.IsCancellationRequested)
            {
                var gameTcp = await _listener.AcceptTcpClientAsync(_stopToken).ConfigureAwait(false);
                if (IsStopped)
                {
                    gameTcp.Dispose();
                    break;
                }

                var remoteAddr = (gameTcp.Client.RemoteEndPoint as IPEndPoint)?.Address;
                if (!DreamPortLanService.IsLocalIPv4(remoteAddr))
                {
                    gameTcp.Dispose();
                    continue;
                }

                gameTcp.NoDelay = true;
                _ = HandleGuestDataConnectionAsync(gameTcp);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            if (!IsStopped)
                EmitEvent("error", $"本地联机监听已停止：{ex.Message}");
        }
    }

    private async Task HandleGuestDataConnectionAsync(TcpClient gameTcp)
    {
        DreamPortLobbyTunnel.RelayStreamConnection? relayConn = null;
        try
        {
            relayConn = await DreamPortLobbyTunnel.RelayStreamConnection.ConnectAsync(_endpoint, _stopToken).ConfigureAwait(false);
            if (IsStopped)
            {
                await relayConn.DisposeAsync().ConfigureAwait(false);
                gameTcp.Dispose();
                return;
            }

            var dataState = new GuestDataConnection(gameTcp, relayConn, conn => _dataConnections.TryRemove(conn, out _));
            _dataConnections[dataState] = 0;

            // 先发 hello 控制帧再转发 Minecraft 原生握手裸字节（中继将校验首个 MC 握手包并回灌给房主）
            await relayConn.SendFrameAsync(DreamPortRelayWire.TypeHello, new
            {
                protocol = DreamPortRelayWire.ProtocolVersion,
                role = DreamPortRelayWire.RoleGuestData,
                room = _roomCode,
                ticket = _ticket,
                peerId = _peerId
            }, _stopToken).ConfigureAwait(false);

            await DreamPortLobbyTunnel.PumpBidirectionalAsync(
                relayConn.Stream,
                gameTcp.GetStream(),
                count => Interlocked.Add(ref _bytesIn, count),
                count => Interlocked.Add(ref _bytesOut, count),
                onFirstRelayBytes: null,
                _stopToken).ConfigureAwait(false);
            await dataState.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!IsStopped)
                EmitEvent("error", $"中继数据连接中断：{ex.Message}");
            if (relayConn is not null)
                await relayConn.DisposeAsync().ConfigureAwait(false);
            gameTcp.Dispose();
        }
    }

    private async Task RunReadControlLoopAsync()
    {
        var terminatedByFrame = false;
        try
        {
            while (!_stopToken.IsCancellationRequested)
            {
                using var frame = await _control.ReadFrameAsync(_stopToken).ConfigureAwait(false);
                if (frame is null)
                    break;

                switch (frame.Type)
                {
                    case DreamPortRelayWire.TypePing:
                        await _control.SendFrameAsync(DreamPortRelayWire.TypePong, null, _stopToken).ConfigureAwait(false);
                        break;

                    case DreamPortRelayWire.TypeHostGone:
                    {
                        var msg = DreamPortLobbyTunnel.GetJsonString(frame.Body, "message");
                        EmitEvent("host-gone", string.IsNullOrWhiteSpace(msg) ? "房主暂时不在游戏中，等待房主回到世界（120 秒宽限期）..." : msg);
                        break;
                    }

                    case DreamPortRelayWire.TypeHostBack:
                    {
                        var msg = DreamPortLobbyTunnel.GetJsonString(frame.Body, "message");
                        EmitEvent("host-back", string.IsNullOrWhiteSpace(msg) ? "房主已回到游戏，联机恢复正常" : msg);
                        break;
                    }

                    case DreamPortRelayWire.TypeKicked:
                    {
                        var reason = DreamPortLobbyTunnel.GetJsonString(frame.Body, "reason");
                        var msg = DreamPortLobbyTunnel.GetJsonString(frame.Body, "message");
                        _kickedReason = DreamPortRelayWire.RejectMessage(reason, msg);
                        EmitEvent("kicked", _kickedReason);
                        terminatedByFrame = true;
                        await StopAsync().ConfigureAwait(false);
                        return;
                    }

                    case DreamPortRelayWire.TypeRoomClosed:
                    {
                        var reason = DreamPortLobbyTunnel.GetJsonString(frame.Body, "reason");
                        var msg = DreamPortLobbyTunnel.GetJsonString(frame.Body, "message");
                        _kickedReason = DreamPortRelayWire.RejectMessage(reason, msg);
                        EmitEvent("room-closed", _kickedReason);
                        terminatedByFrame = true;
                        await StopAsync().ConfigureAwait(false);
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal stop
        }
        catch (Exception ex)
        {
            if (!IsStopped)
                LogWrapper.Debug(ex, "DreamPort", "访客控制连接读取异常");
        }
        finally
        {
            if (!terminatedByFrame && !IsStopped)
            {
                EmitEvent("room-closed", _kickedReason ?? "房主已结束联机");
            }

            await StopAsync().ConfigureAwait(false);
        }
    }

    private async Task RunHeartbeatLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(_stopToken).ConfigureAwait(false))
            {
                await _control.SendFrameAsync(DreamPortRelayWire.TypePing, null, _stopToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // normal stop
        }
    }

    private void EmitEvent(string kind, string message)
    {
        try { _onEvent?.Invoke(new DreamPortLobbyTunnelEvent(kind, message, _peerId)); } catch { /* ignore */ }
    }

    private sealed class GuestDataConnection(
        TcpClient gameTcp,
        DreamPortLobbyTunnel.RelayStreamConnection relayConn,
        Action<GuestDataConnection> onDisposed) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            onDisposed(this);
            try { gameTcp.Dispose(); } catch { /* ignore */ }
            await relayConn.DisposeAsync().ConfigureAwait(false);
        }
    }
}
