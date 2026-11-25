using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DreamPort.Core.Api;
using DreamPort.Core.Lobby;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamPort.Test;

[TestClass]
public sealed class DreamPortLobbyWireAndLanTests
{
    [TestMethod]
    public async Task RelayWire_EncodeDecodeRoundtrip_AndRejectsMalformedOrOversizedFrames()
    {
        // Roundtrip with body and without body
        using var ms = new MemoryStream();
        await DreamPortRelayWire.WriteFrameAsync(ms, DreamPortRelayWire.TypeHello, new
        {
            protocol = DreamPortRelayWire.ProtocolVersion,
            role = DreamPortRelayWire.RoleHostControl,
            room = "AB234567"
        });
        await DreamPortRelayWire.WriteFrameAsync(ms, DreamPortRelayWire.TypePing);

        ms.Position = 0;
        using var f1 = await DreamPortRelayWire.ReadFrameAsync(ms);
        Assert.IsNotNull(f1);
        Assert.AreEqual(DreamPortRelayWire.TypeHello, f1.Type);
        Assert.AreEqual("AB234567", f1.Body.GetProperty("room").GetString());

        using var f2 = await DreamPortRelayWire.ReadFrameAsync(ms);
        Assert.IsNotNull(f2);
        Assert.AreEqual(DreamPortRelayWire.TypePing, f2.Type);
        Assert.AreEqual(JsonValueKind.Undefined, f2.Body.ValueKind);

        Assert.IsNull(await DreamPortRelayWire.ReadFrameAsync(ms));

        // Truncated header throws EndOfStreamException
        using var truncated = new MemoryStream([0x00, 0x01]);
        try
        {
            await DreamPortRelayWire.ReadFrameAsync(truncated);
            Assert.Fail("Expected EndOfStreamException");
        }
        catch (EndOfStreamException)
        {
        }

        // Zero-length header throws InvalidDataException
        using var zeroLen = new MemoryStream([0x00, 0x00, 0x00, 0x00]);
        try
        {
            await DreamPortRelayWire.ReadFrameAsync(zeroLen);
            Assert.Fail("Expected InvalidDataException");
        }
        catch (InvalidDataException)
        {
        }

        // Oversized (> 64KB) header throws InvalidDataException
        using var overLen = new MemoryStream([0x00, 0x01, 0x00, 0x01]);
        try
        {
            await DreamPortRelayWire.ReadFrameAsync(overLen);
            Assert.Fail("Expected InvalidDataException");
        }
        catch (InvalidDataException)
        {
        }

        // Invalid JSON body throws InvalidDataException
        using var badJson = new MemoryStream();
        badJson.Write([0x00, 0x00, 0x00, 0x03]);
        badJson.Write("{xx"u8);
        badJson.Position = 0;
        try
        {
            await DreamPortRelayWire.ReadFrameAsync(badJson);
            Assert.Fail("Expected InvalidDataException");
        }
        catch (InvalidDataException)
        {
        }

        // Valid JSON missing "type" throws InvalidDataException
        using var noType = new MemoryStream();
        var noTypeBytes = "{\"body\":1}"u8.ToArray();
        noType.Write([(byte)(noTypeBytes.Length >> 24), (byte)(noTypeBytes.Length >> 16), (byte)(noTypeBytes.Length >> 8), (byte)noTypeBytes.Length]);
        noType.Write(noTypeBytes);
        noType.Position = 0;
        try
        {
            await DreamPortRelayWire.ReadFrameAsync(noType);
            Assert.Fail("Expected InvalidDataException");
        }
        catch (InvalidDataException)
        {
        }
    }

    [TestMethod]
    public void RelayWire_RejectMessage_MapsAllKnownReasonsAndDistinguishesHostKickedVsBanned()
    {
        var kickedMsg = DreamPortRelayWire.RejectMessage(DreamPortRelayWire.ReasonHostKicked);
        var bannedMsg = DreamPortRelayWire.RejectMessage(DreamPortRelayWire.ReasonBanned);

        Assert.AreNotEqual(kickedMsg, bannedMsg);
        StringAssert.Contains(kickedMsg, "房主");
        StringAssert.Contains(bannedMsg, "限制");

        Assert.AreEqual("房间不存在或已关闭", DreamPortRelayWire.RejectMessage(DreamPortRelayWire.ReasonRoomNotFound));
        Assert.AreEqual("房间人数已满", DreamPortRelayWire.RejectMessage(DreamPortRelayWire.ReasonRoomFull));
        Assert.AreEqual("该房间为仅限好友加入，需要房间码", DreamPortRelayWire.RejectMessage(DreamPortRelayWire.ReasonRoomPrivate));
        Assert.AreEqual("房票校验失败，请重新登录梦港账号后再试", DreamPortRelayWire.RejectMessage(DreamPortRelayWire.ReasonTicketInvalid));
        Assert.AreEqual("房票已过期，请刷新大厅或重新发起联机", DreamPortRelayWire.RejectMessage(DreamPortRelayWire.ReasonTicketExpired));
        Assert.AreEqual("当前中继在线总人数已满，请稍后再试", DreamPortRelayWire.RejectMessage(DreamPortRelayWire.ReasonSiteFull));
        Assert.AreEqual("梦港联机服务正在维护，请稍后再试", DreamPortRelayWire.RejectMessage(DreamPortRelayWire.ReasonRelayMaintenance));
        Assert.AreEqual("客户端与联机服务协议版本不兼容，请更新启动器", DreamPortRelayWire.RejectMessage(DreamPortRelayWire.ReasonUnsupportedVersion));
        Assert.AreEqual("连接流量不符合 Minecraft 协议特征，已被中继拒绝", DreamPortRelayWire.RejectMessage(DreamPortRelayWire.ReasonNotMinecraft));
        Assert.AreEqual("当前并发连接数过多，请稍后再试", DreamPortRelayWire.RejectMessage(DreamPortRelayWire.ReasonTooManyConnections));
        Assert.AreEqual("操作过于频繁，请稍后再试", DreamPortRelayWire.RejectMessage(DreamPortRelayWire.ReasonRateLimited));
        Assert.AreEqual("当前网络已被中继临时限制，请稍后再试", DreamPortRelayWire.RejectMessage(DreamPortRelayWire.ReasonIpBanned));
        Assert.AreEqual("对端连接已断开", DreamPortRelayWire.RejectMessage(DreamPortRelayWire.ReasonPeerGone));
        Assert.AreEqual("房主已结束联机", DreamPortRelayWire.RejectMessage(DreamPortRelayWire.ReasonRoomClosed));
        Assert.AreEqual("房主暂时不在游戏中", DreamPortRelayWire.RejectMessage(DreamPortRelayWire.ReasonHostGone));
        Assert.AreEqual("custom fallback", DreamPortRelayWire.RejectMessage("UNKNOWN_CODE", " custom fallback "));
    }

    [TestMethod]
    public void LanServiceAndRoomCodeHelpers_ValidateInputsProperly()
    {
        var ok = DreamPortLanService.CheckLanAdvertisement(
            "[MOTD]My World[/MOTD][AD]25565[/AD]\0",
            IPAddress.Loopback,
            ip => IPAddress.IsLoopback(ip));
        Assert.IsTrue(ok.Ok);
        Assert.AreEqual(25565, ok.Port);

        var remoteRejected = DreamPortLanService.CheckLanAdvertisement(
            "[MOTD]Other[/MOTD][AD]25565[/AD]",
            IPAddress.Parse("192.168.1.99"),
            _ => false);
        Assert.IsFalse(remoteRejected.Ok);

        var badFormat = DreamPortLanService.CheckLanAdvertisement("hello", IPAddress.Loopback, _ => true);
        Assert.IsFalse(badFormat.Ok);

        var badPort = DreamPortLanService.CheckLanAdvertisement("[MOTD]X[/MOTD][AD]70000[/AD]", IPAddress.Loopback, _ => true);
        Assert.IsFalse(badPort.Ok);

        Assert.AreEqual("房间  测试", DreamPortLanService.SanitizeMotd("[MOTD]房间\r\n[/AD]测试"));
        Assert.AreEqual(48, DreamPortLanService.SanitizeMotd(new string('A', 80)).Length);
        Assert.AreEqual("梦港联机房间", DreamPortLanService.SanitizeMotd("  [AD][/AD]  "));

        Assert.AreEqual(25565, DreamPortLanService.ParseLocalPort(" 25565 "));
        Assert.IsNull(DreamPortLanService.ParseLocalPort("0"));
        Assert.IsNull(DreamPortLanService.ParseLocalPort("65536"));
        Assert.IsNull(DreamPortLanService.ParseLocalPort("-1"));
        Assert.IsNull(DreamPortLanService.ParseLocalPort("abc"));

        Assert.AreEqual("AB234567", DreamPortApiClient.NormalizeRoomCode(" ab-23 45_67 "));
        Assert.IsTrue(DreamPortApiClient.IsValidRoomCodeFormat("AB234567"));
        Assert.IsFalse(DreamPortApiClient.IsValidRoomCodeFormat("AB23I567"));
        Assert.IsFalse(DreamPortApiClient.IsValidRoomCodeFormat("AB23O567"));
        Assert.IsFalse(DreamPortApiClient.IsValidRoomCodeFormat("AB23L567"));
        Assert.IsFalse(DreamPortApiClient.IsValidRoomCodeFormat("AB23U567"));
        Assert.IsFalse(DreamPortApiClient.IsValidRoomCodeFormat("SHORT"));
    }

    [TestMethod]
    public async Task LanPortDetection_LogFallbackDoesNotLeakUnobservedUdpCancellation()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var root = Path.Combine(Path.GetTempPath(), "pcl-lan-detect-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(
            Path.Combine(logs, "latest.log"),
            $"Published LAN server on port {port}\n");

        var leaked = 0;
        void OnUnobserved(object? _, UnobservedTaskExceptionEventArgs args)
        {
            if (args.Exception.Flatten().InnerExceptions.Any(ex => ex is SocketException))
                Interlocked.Increment(ref leaked);
            args.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var result = await DreamPortLanService.DetectLanPortAsync(
                TimeSpan.FromSeconds(4),
                [root]);

            Assert.AreEqual(port, result.Port);
            for (var i = 0; i < 5; i++)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
                await Task.Delay(20);
            }

            Assert.AreEqual(0, Volatile.Read(ref leaked), "UDP 接收取消异常必须被观察");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
            listener.Stop();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task EndToEndLoopback_HostAndGuestStreamRawBytes_ReconnectsAndHandlesEventsCleanly()
    {
        await using var gameServer = new LoopbackEchoGameServer();
        await using var relay = new SimulatedRelayServer();

        await using var hostTunnel = new DreamPortLobbyTunnel();
        await using var guestTunnel = new DreamPortLobbyTunnel();

        var guestEvents = new ConcurrentQueue<DreamPortLobbyTunnelEvent>();

        var hostSession = await hostTunnel.StartHostAsync(
            endpoint: relay.Endpoint,
            roomCode: "AB234567",
            ticket: "t-host",
            playerName: "HostPlayer",
            localGamePort: gameServer.Port);

        Assert.IsTrue(hostTunnel.IsBusy);
        await relay.WaitForHostControlAsync(TimeSpan.FromSeconds(5));

        var guestSession = await guestTunnel.StartGuestAsync(
            endpoint: relay.Endpoint,
            roomCode: "AB234567",
            ticket: "t-guest",
            playerName: "GuestPlayer",
            preferLocalPort: 0,
            advertiseName: "TestWorld",
            onEvent: ev => guestEvents.Enqueue(ev));

        Assert.IsTrue(guestTunnel.IsBusy);
        Assert.IsTrue(guestSession.LocalPort > 0);
        await relay.WaitForGuestControlAsync(TimeSpan.FromSeconds(5));

        // ① Send peer_added -> host dials eager host-data -> guest client connects & sends bytes
        await relay.SendToHostControlAsync(DreamPortRelayWire.TypePeerAdded, new
        {
            peerId = "peer-1",
            playerName = "GuestPlayer"
        });
        await relay.WaitForPendingHostDataAsync("peer-1", TimeSpan.FromSeconds(5));

        await AssertRoundtripPayloadAsync(guestSession.LocalPort, "MC-HANDSHAKE-V1");

        // Poll briefly for stats counters to reflect transferred bytes
        var statsReady = await WaitUntilAsync(
            () => hostSession.GetStats().BytesIn > 0
                && hostSession.GetStats().BytesOut > 0
                && guestSession.GetStats().BytesIn > 0
                && guestSession.GetStats().BytesOut > 0,
            TimeSpan.FromSeconds(3));
        Assert.IsTrue(statsReady, "Expected non-zero relay traffic stats after payload exchange");

        // ② Reconnect path via peer_data_request
        await relay.SendToHostControlAsync(DreamPortRelayWire.TypePeerDataRequest, new
        {
            peerId = "peer-1"
        });
        await relay.WaitForPendingHostDataAsync("peer-1", TimeSpan.FromSeconds(5));

        await AssertRoundtripPayloadAsync(guestSession.LocalPort, "MC-RECONNECT-V2");

        // ③ Grace-period host_gone / host_back frames reach guest events
        await relay.SendToGuestControlAsync(DreamPortRelayWire.TypeHostGone, new { message = "host paused" });
        await relay.SendToGuestControlAsync(DreamPortRelayWire.TypeHostBack, new { message = "host resumed" });

        Assert.IsTrue(await WaitUntilAsync(
            () => guestEvents.Any(e => e.Kind == "host-gone") && guestEvents.Any(e => e.Kind == "host-back"),
            TimeSpan.FromSeconds(3)));

        // ④ Host kick_member and unkick_member frames are received by simulated relay
        await hostSession.KickMemberAsync("GuestPlayer", "afk");
        await hostSession.UnkickMemberAsync("GuestPlayer");
        Assert.IsTrue(await WaitUntilAsync(
            () => relay.ReceivedKickTargets.Contains("GuestPlayer") && relay.ReceivedUnkickTargets.Contains("GuestPlayer"),
            TimeSpan.FromSeconds(3)));

        // ⑤ room_closed terminates guest cleanly and resets IsBusy
        await relay.SendToGuestControlAsync(DreamPortRelayWire.TypeRoomClosed, new { reason = DreamPortRelayWire.ReasonRoomClosed });
        Assert.IsTrue(await WaitUntilAsync(
            () => !guestTunnel.IsBusy && guestEvents.Any(e => e.Kind == "room-closed"),
            TimeSpan.FromSeconds(3)));

        await hostTunnel.StopAllAsync();
        Assert.IsFalse(hostTunnel.IsBusy);
    }

    private static async Task AssertRoundtripPayloadAsync(int port, string message)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();

        var request = Encoding.UTF8.GetBytes(message);
        await stream.WriteAsync(request);
        await stream.FlushAsync();

        var expected = Encoding.UTF8.GetBytes("ECHO:" + message);
        var actual = new byte[expected.Length];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await stream.ReadExactlyAsync(actual, cts.Token);
        CollectionAssert.AreEqual(expected, actual);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(25);
        }

        return condition();
    }

    internal sealed class LoopbackEchoGameServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly CancellationToken _token;

        public LoopbackEchoGameServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _token = _cts.Token;
            _ = AcceptLoopAsync();
        }

        public int Port { get; }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_token.IsCancellationRequested)
                {
                    var tcp = await _listener.AcceptTcpClientAsync(_token).ConfigureAwait(false);
                    _ = HandleClientAsync(tcp);
                }
            }
            catch
            {
                // stopped
            }
        }

        private async Task HandleClientAsync(TcpClient tcp)
        {
            using (tcp)
            {
                try
                {
                    tcp.NoDelay = true;
                    using var stream = tcp.GetStream();
                    var buf = new byte[4096];
                    var prefix = "ECHO:"u8.ToArray();
                    while (!_token.IsCancellationRequested)
                    {
                        var read = await stream.ReadAsync(buf, _token).ConfigureAwait(false);
                        if (read == 0)
                            break;
                        await stream.WriteAsync(prefix, _token).ConfigureAwait(false);
                        await stream.WriteAsync(buf.AsMemory(0, read), _token).ConfigureAwait(false);
                        await stream.FlushAsync(_token).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // closed
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            try { _listener.Dispose(); } catch { }
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class SimulatedRelayServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly CancellationToken _token;
        private readonly TaskCompletionSource _hostControlReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _guestControlReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, Channel<TcpClient>> _pendingHostData = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _hostWriteLock = new(1, 1);
        private readonly SemaphoreSlim _guestWriteLock = new(1, 1);

        private NetworkStream? _hostControlStream;
        private NetworkStream? _guestControlStream;

        public SimulatedRelayServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Endpoint = new DreamPortRelayEndpoint("127.0.0.1", port, Tls: false);
            _token = _cts.Token;
            _ = AcceptLoopAsync();
        }

        public DreamPortRelayEndpoint Endpoint { get; }
        public ConcurrentQueue<string> ReceivedKickTargets { get; } = new();
        public ConcurrentQueue<string> ReceivedUnkickTargets { get; } = new();

        public Task WaitForHostControlAsync(TimeSpan timeout) => _hostControlReady.Task.WaitAsync(timeout);
        public Task WaitForGuestControlAsync(TimeSpan timeout) => _guestControlReady.Task.WaitAsync(timeout);

        public async Task WaitForPendingHostDataAsync(string peerId, TimeSpan timeout)
        {
            var channel = _pendingHostData.GetOrAdd(peerId, _ => Channel.CreateUnbounded<TcpClient>());
            using var cts = new CancellationTokenSource(timeout);
            await channel.Reader.WaitToReadAsync(cts.Token).ConfigureAwait(false);
        }

        public async Task SendToHostControlAsync(string type, object? body = null)
        {
            var stream = _hostControlStream ?? throw new InvalidOperationException("host-control not connected");
            await _hostWriteLock.WaitAsync(_token).ConfigureAwait(false);
            try
            {
                await DreamPortRelayWire.WriteFrameAsync(stream, type, body, _token).ConfigureAwait(false);
            }
            finally
            {
                _hostWriteLock.Release();
            }
        }

        public async Task SendToGuestControlAsync(string type, object? body = null)
        {
            var stream = _guestControlStream ?? throw new InvalidOperationException("guest-control not connected");
            await _guestWriteLock.WaitAsync(_token).ConfigureAwait(false);
            try
            {
                await DreamPortRelayWire.WriteFrameAsync(stream, type, body, _token).ConfigureAwait(false);
            }
            finally
            {
                _guestWriteLock.Release();
            }
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_token.IsCancellationRequested)
                {
                    var tcp = await _listener.AcceptTcpClientAsync(_token).ConfigureAwait(false);
                    tcp.NoDelay = true;
                    _ = HandleConnAsync(tcp);
                }
            }
            catch
            {
                // stopped
            }
        }

        private async Task HandleConnAsync(TcpClient tcp)
        {
            try
            {
                var stream = tcp.GetStream();
                using var hello = await DreamPortRelayWire.ReadFrameAsync(stream, _token).ConfigureAwait(false);
                if (hello is null || !string.Equals(hello.Type, DreamPortRelayWire.TypeHello, StringComparison.Ordinal))
                {
                    tcp.Dispose();
                    return;
                }

                var role = hello.Body.GetProperty("role").GetString() ?? string.Empty;
                var peerId = hello.Body.TryGetProperty("peerId", out var pidEl) ? pidEl.GetString() ?? "peer-1" : "peer-1";

                switch (role)
                {
                    case DreamPortRelayWire.RoleHostControl:
                        _hostControlStream = stream;
                        await SendToHostControlAsync(DreamPortRelayWire.TypeHelloAck, new { protocol = 1, peerId = "host" }).ConfigureAwait(false);
                        _hostControlReady.TrySetResult();
                        await ReadHostControlLoopAsync(tcp, stream).ConfigureAwait(false);
                        break;

                    case DreamPortRelayWire.RoleGuestControl:
                        _guestControlStream = stream;
                        await SendToGuestControlAsync(DreamPortRelayWire.TypeHelloAck, new { protocol = 1, peerId = "peer-1" }).ConfigureAwait(false);
                        _guestControlReady.TrySetResult();
                        await ReadGuestControlLoopAsync(tcp, stream).ConfigureAwait(false);
                        break;

                    case DreamPortRelayWire.RoleHostData:
                        await _pendingHostData.GetOrAdd(peerId, _ => Channel.CreateUnbounded<TcpClient>())
                            .Writer.WriteAsync(tcp, _token).ConfigureAwait(false);
                        break;

                    case DreamPortRelayWire.RoleGuestData:
                    {
                        var channel = _pendingHostData.GetOrAdd(peerId, _ => Channel.CreateUnbounded<TcpClient>());
                        using var pairCts = CancellationTokenSource.CreateLinkedTokenSource(_token);
                        pairCts.CancelAfter(TimeSpan.FromSeconds(5));
                        var hostTcp = await channel.Reader.ReadAsync(pairCts.Token).ConfigureAwait(false);
                        await PumpRawPairAsync(tcp, hostTcp).ConfigureAwait(false);
                        break;
                    }

                    default:
                        tcp.Dispose();
                        break;
                }
            }
            catch
            {
                tcp.Dispose();
            }
        }

        private async Task ReadHostControlLoopAsync(TcpClient tcp, NetworkStream stream)
        {
            using (tcp)
            {
                while (!_token.IsCancellationRequested)
                {
                    using var frame = await DreamPortRelayWire.ReadFrameAsync(stream, _token).ConfigureAwait(false);
                    if (frame is null)
                        break;

                    switch (frame.Type)
                    {
                        case DreamPortRelayWire.TypePing:
                            await SendToHostControlAsync(DreamPortRelayWire.TypePong).ConfigureAwait(false);
                            break;
                        case DreamPortRelayWire.TypeKickMember:
                        {
                            var name = frame.Body.TryGetProperty("playerName", out var p) ? p.GetString() ?? string.Empty : string.Empty;
                            if (!string.IsNullOrEmpty(name))
                                ReceivedKickTargets.Enqueue(name);
                            await SendToHostControlAsync(DreamPortRelayWire.TypeKickResult, new { ok = true, playerName = name }).ConfigureAwait(false);
                            break;
                        }
                        case DreamPortRelayWire.TypeUnkickMember:
                        {
                            var name = frame.Body.TryGetProperty("playerName", out var p) ? p.GetString() ?? string.Empty : string.Empty;
                            if (!string.IsNullOrEmpty(name))
                                ReceivedUnkickTargets.Enqueue(name);
                            break;
                        }
                    }
                }
            }
        }

        private async Task ReadGuestControlLoopAsync(TcpClient tcp, NetworkStream stream)
        {
            using (tcp)
            {
                while (!_token.IsCancellationRequested)
                {
                    using var frame = await DreamPortRelayWire.ReadFrameAsync(stream, _token).ConfigureAwait(false);
                    if (frame is null)
                        break;
                    if (frame.Type == DreamPortRelayWire.TypePing)
                        await SendToGuestControlAsync(DreamPortRelayWire.TypePong).ConfigureAwait(false);
                }
            }
        }

        private async Task PumpRawPairAsync(TcpClient a, TcpClient b)
        {
            using (a)
            using (b)
            {
                var sa = a.GetStream();
                var sb = b.GetStream();
                var t1 = CopyHalfAsync(sa, sb, b);
                var t2 = CopyHalfAsync(sb, sa, a);
                await Task.WhenAll(t1, t2).ConfigureAwait(false);
            }
        }

        private async Task CopyHalfAsync(NetworkStream src, NetworkStream dst, TcpClient dstTcp)
        {
            var buf = new byte[16 * 1024];
            try
            {
                while (!_token.IsCancellationRequested)
                {
                    var n = await src.ReadAsync(buf, _token).ConfigureAwait(false);
                    if (n == 0)
                        break;
                    await dst.WriteAsync(buf.AsMemory(0, n), _token).ConfigureAwait(false);
                    await dst.FlushAsync(_token).ConfigureAwait(false);
                }
            }
            catch
            {
                // closed
            }
            finally
            {
                try { dstTcp.Client.Shutdown(SocketShutdown.Send); } catch { }
            }
        }

        public ValueTask DisposeAsync()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            try { _listener.Dispose(); } catch { }
            return ValueTask.CompletedTask;
        }
    }
}
