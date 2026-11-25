using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DreamPort.Core.Api;
using DreamPort.Core.Auth;
using DreamPort.Core.Lobby;
using DreamPort.Core.Realtime;
using DreamPort.Core.Services;
using DreamPort.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamPort.Test;

[TestClass]
public sealed class DreamPortRealtimeAndLobbyEnhancementTests
{
    [TestMethod]
    public void RealtimeClient_BuildRealtimeUri_NormalizesSchemesAndPorts()
    {
        Assert.AreEqual("wss://xmcraft.cn/realtime", DreamPortRealtimeClient.BuildRealtimeUri("https://xmcraft.cn/").ToString());
        Assert.AreEqual("ws://127.0.0.1:18899/realtime", DreamPortRealtimeClient.BuildRealtimeUri("http://127.0.0.1:18899/api?foo=1").ToString());
        Assert.AreEqual("ws://localhost:8080/realtime", DreamPortRealtimeClient.BuildRealtimeUri("http://localhost:8080").ToString());
    }

    [TestMethod]
    public async Task RealtimeClient_AuthenticatesReceivesChangedReconnectsAndStopsCleanly()
    {
        await using var wsServer = new LoopbackWebSocketServer();
        var events = new ConcurrentQueue<string>();
        var statuses = new ConcurrentQueue<bool>();

        await using var client = new DreamPortRealtimeClient(
            getBaseUrl: () => $"http://127.0.0.1:{wsServer.Port}",
            getToken: () => "tok-realtime-1",
            onChanged: ev => events.Enqueue(ev),
            onStatusChanged: st => statuses.Enqueue(st),
            baseReconnectMs: 60);

        client.Start();
        await wsServer.WaitForConnectionCountAsync(1, TimeSpan.FromSeconds(5));
        Assert.IsTrue(await WaitUntilAsync(() => events.Contains("sync") && client.IsConnected, TimeSpan.FromSeconds(5)));

        await wsServer.BroadcastToLatestAsync(new { type = "changed", eventName = "unused" });
        await wsServer.BroadcastRawJsonToLatestAsync("{\"type\":\"changed\",\"event\":\"lobby-status\"}");
        Assert.IsTrue(await WaitUntilAsync(() => events.Contains("lobby-status"), TimeSpan.FromSeconds(3)));

        // Force-close first socket to verify exponential backoff reconnect
        await wsServer.DropLatestSocketAsync();
        await wsServer.WaitForConnectionCountAsync(2, TimeSpan.FromSeconds(5));
        Assert.IsTrue(await WaitUntilAsync(() => client.IsConnected && statuses.Contains(false), TimeSpan.FromSeconds(5)));

        await wsServer.BroadcastRawJsonToLatestAsync("{\"type\":\"changed\",\"event\":\"invite\"}");
        Assert.IsTrue(await WaitUntilAsync(() => events.Contains("invite"), TimeSpan.FromSeconds(3)));

        await client.StopAsync();
        Assert.IsFalse(client.IsConnected);
    }

    [TestMethod]
    public async Task LanLogFallback_ParsesLastValidPortAndFiltersStaleLogsWithProbe()
    {
        var logText = """
            [12:00:00] [Render thread/INFO]: Published LAN server on port 70000
            [12:00:01] [Render thread/INFO]: Published LAN server on port 51234
            [12:05:00] [Render thread/INFO]: [System] [CHAT] 此世界现已对局域网开放。端口号为[59930]
            """;
        Assert.AreEqual(59930, DreamPortLanService.ParseLanPortFromLog(logText));
        Assert.IsNull(DreamPortLanService.ParseLanPortFromLog("nothing useful or port 0"));

        var tempDir = Path.Combine(Path.GetTempPath(), "dp-lan-log-" + Guid.NewGuid().ToString("N"));
        var staleDir = Path.Combine(tempDir, "stale-inst");
        var freshDir = Path.Combine(tempDir, "fresh-inst");
        Directory.CreateDirectory(Path.Combine(staleDir, "logs"));
        Directory.CreateDirectory(Path.Combine(freshDir, "logs"));

        using var fakeGameListener = new TcpListener(IPAddress.Loopback, 0);
        fakeGameListener.Start();
        var livePort = ((IPEndPoint)fakeGameListener.LocalEndpoint).Port;

        using var fakeStaleListener = new TcpListener(IPAddress.Loopback, 0);
        fakeStaleListener.Start();
        var stalePort = ((IPEndPoint)fakeStaleListener.LocalEndpoint).Port;

        try
        {
            var staleLogPath = Path.Combine(staleDir, "logs", "latest.log");
            File.WriteAllText(staleLogPath, $"Published LAN server on port {stalePort}\n");
            File.SetLastWriteTimeUtc(staleLogPath, DateTime.UtcNow.AddHours(-12));

            var freshLogPath = Path.Combine(freshDir, "logs", "latest.log");
            File.WriteAllText(freshLogPath, $"Published LAN server on port 54321\n[CHAT] 端口号为[{livePort}]\n");

            var detected = await DreamPortLanService.DetectLanPortFromLogsAsync([staleDir, freshDir]);
            Assert.AreEqual(livePort, detected);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task GameInfoService_ModHashJarScanSlpAndCompatibilityRulesWorkEndToEnd()
    {
        Assert.AreEqual(string.Empty, DreamPortGameInfoService.ComputeModHash([]));
        Assert.AreEqual(
            DreamPortGameInfoService.ComputeModHash(["sodium", "iris"]),
            DreamPortGameInfoService.ComputeModHash(["iris", "sodium"]));
        Assert.AreEqual("d4d8adf9", DreamPortGameInfoService.ComputeModHash(["alpha", "beta"]));

        // Read mods/*.jar skipping .disabled
        var tempDir = Path.Combine(Path.GetTempPath(), "dp-mods-" + Guid.NewGuid().ToString("N"));
        var modsDir = Path.Combine(tempDir, "mods");
        Directory.CreateDirectory(modsDir);
        try
        {
            CreateTestJar(Path.Combine(modsDir, "sodium.jar"), "fabric.mod.json", "{\"id\":\"sodium\"}");
            CreateTestJar(Path.Combine(modsDir, "lithium.jar.disabled"), "fabric.mod.json", "{\"id\":\"lithium\"}");
            CreateTestJar(Path.Combine(modsDir, "create.jar"), "META-INF/neoforge.mods.toml", "modId=\"create\"\n");

            var (mods, loader) = DreamPortGameInfoService.ReadLocalMods(tempDir);
            Assert.IsTrue(mods.Contains("sodium"));
            Assert.IsTrue(mods.Contains("create"));
            Assert.IsFalse(mods.Contains("lithium"));
            Assert.AreEqual("neoforge", loader);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }

        // Loopback SLP query test
        using var slpServer = new LoopbackSlpServer(new
        {
            version = new { name = "1.21.4", protocol = 769 },
            forgeData = new { mods = new[] { new { modId = "jei" }, new { modId = "create" } } }
        });
        var slp = await DreamPortGameInfoService.QueryLocalSlpAsync(slpServer.Port, timeoutMs: 3000);
        Assert.IsNotNull(slp);
        Assert.AreEqual("1.21.4", slp.Version);
        Assert.AreEqual("forge", slp.Loader);
        CollectionAssert.AreEquivalent(new[] { "jei", "create" }, slp.Mods.ToArray());

        // Room compatibility ranking + Forge/NeoForge hard block vs Fabric soft warning
        var myHash = DreamPortGameInfoService.ComputeModHash(["jei", "create"]);
        var exactForgeRoom = new DreamPortLobbyRoom("EXACT234", "H", "Exact", "public", 8, "1.21.4", "forge", myHash, ["jei", "create"], "open", 100, 1, false);
        var mismatchForgeRoom = new DreamPortLobbyRoom("MIS23456", "H", "Missing", "public", 8, "1.21.4", "forge", "deadbeef", ["jei", "create", "ae2"], "open", 100, 1, false);
        var fabricModDiffRoom = new DreamPortLobbyRoom("FAB23456", "H", "Fabric", "public", 8, "1.21.4", "fabric", "11223344", ["sodium"], "open", 100, 1, false);

        var cExact = DreamPortGameInfoService.EvaluateRoomCompatibility(exactForgeRoom, "1.21.4", "forge", myHash, ["jei", "create"]);
        Assert.AreEqual(3, cExact.MatchScore);
        Assert.IsFalse(cExact.IsHardBlocked);

        var cForgeMissing = DreamPortGameInfoService.EvaluateRoomCompatibility(mismatchForgeRoom, "1.21.4", "forge", myHash, ["jei", "create"]);
        Assert.IsTrue(cForgeMissing.IsHardBlocked);
        CollectionAssert.Contains(cForgeMissing.MissingMods.ToArray(), "ae2");

        var cLoaderWrong = DreamPortGameInfoService.EvaluateRoomCompatibility(exactForgeRoom, "1.21.4", "fabric", myHash, ["jei", "create"]);
        Assert.IsTrue(cLoaderWrong.IsHardBlocked);

        var cFabricWarn = DreamPortGameInfoService.EvaluateRoomCompatibility(fabricModDiffRoom, "1.21.4", "fabric", myHash, ["jei", "create"]);
        Assert.IsFalse(cFabricWarn.IsHardBlocked);
        Assert.IsTrue(cFabricWarn.HasSoftWarning);
        Assert.AreEqual(2, cFabricWarn.MatchScore);
    }

    [TestMethod]
    public async Task LobbyViewModel_SortsRoomsByCompatibility_HardBlocksIncompatibleForge_AndSendsModMetadataOnHost()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dp-lobby-compat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        using var localGame = new TcpListener(IPAddress.Loopback, 0);
        localGame.Start();
        var gamePort = ((IPEndPoint)localGame.LocalEndpoint).Port;

        string? postedModHash = null;
        string? postedModLoader = null;
        var joinCalls = 0;

        try
        {
            var store = new DreamPortSessionStore(tempDir, new InMemoryVault());
            store.Save("https://xmcraft.cn", "Alex", false, "tok-compat");

            var roomVerMismatch = new
            {
                code = "VER23456",
                host = "A",
                name = "Old 1.20 Room",
                visibility = "public",
                maxPlayers = 8,
                mcVersion = "1.20.1",
                modLoader = "vanilla",
                modHash = "",
                modList = Array.Empty<string>(),
                status = "open",
                createdAt = 5000L
            };
            var roomExactMatch = new
            {
                code = "EXP23456",
                host = "B",
                name = "Matching 1.21.4 Room",
                visibility = "public",
                maxPlayers = 8,
                mcVersion = "1.21.4",
                modLoader = "vanilla",
                modHash = "",
                modList = Array.Empty<string>(),
                status = "open",
                createdAt = 1000L
            };
            var roomHardForge = new
            {
                code = "FRG23456",
                host = "C",
                name = "Strict Forge Room",
                visibility = "public",
                maxPlayers = 8,
                mcVersion = "1.21.4",
                modLoader = "forge",
                modHash = "12345678",
                modList = new[] { "jei" },
                status = "open",
                createdAt = 6000L
            };

            await using var vm = new DreamPortLobbyViewModel(
                store: store,
                clientFactory: url => new DreamPortApiClient(url, new StubHttpHandler((req, _) =>
                {
                    var path = req.RequestUri!.AbsolutePath;
                    if (path == "/api/lobby/config")
                    {
                        return JsonResponse(HttpStatusCode.OK, new
                        {
                            success = true,
                            data = new { enabled = true, relayHost = "127.0.0.1", relayPort = 17800, relayTls = false, maxPlayersPerRoom = 8 }
                        });
                    }
                    if (path == "/api/lobby/rooms" && req.Method == HttpMethod.Get)
                    {
                        return JsonResponse(HttpStatusCode.OK, new
                        {
                            success = true,
                            data = new { rooms = new object[] { roomVerMismatch, roomHardForge, roomExactMatch } }
                        });
                    }
                    if (path == "/api/lobby/rooms" && req.Method == HttpMethod.Post)
                    {
                        var bodyText = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                        using var doc = JsonDocument.Parse(bodyText);
                        postedModHash = doc.RootElement.GetProperty("modHash").GetString();
                        postedModLoader = doc.RootElement.GetProperty("modLoader").GetString();
                        return JsonResponse(HttpStatusCode.BadRequest, new { success = false, message = "stop-after-capture" });
                    }
                    if (path == "/api/lobby/rooms/FRG23456/join")
                    {
                        joinCalls++;
                        return JsonResponse(HttpStatusCode.OK, new
                        {
                            success = true,
                            data = new { ticket = "t-forge", ticketTtlSeconds = 60, room = roomHardForge }
                        });
                    }
                    return JsonResponse(HttpStatusCode.OK, new { success = true, data = new { friends = Array.Empty<object>(), incoming = Array.Empty<object>(), outgoing = Array.Empty<object>() } });
                }), disposeHandler: true),
                getSelectedInstanceInfo: () => ("1.21.4", "vanilla"),
                inspectGameInfo: (_, _, _, _, _) => Task.FromResult(
                    new DreamPortGameInfo("1.21.4", ["sodium"], "cafebabe", "fabric", "slp")));

            await vm.InitializeOrSyncAsync();

            Assert.HasCount(3, vm.PublicRooms);
            Assert.AreEqual("EXP23456", vm.PublicRooms[0].Code);
            Assert.AreEqual("完全兼容", vm.PublicRooms[0].CompatibilityBadge);
            Assert.IsTrue(vm.PublicRooms.First(r => r.Code == "FRG23456").IsHardBlocked);

            // Attempt joining hard-blocked Forge room from vanilla instance -> rejected cleanly without connecting relay
            vm.JoinRoomCodeInput = "FRG23456";
            await vm.JoinByCodeCommand.ExecuteAsync(null);
            Assert.AreEqual(1, joinCalls);
            Assert.IsFalse(vm.IsGuest);
            Assert.IsFalse(vm.HasPendingVersionMismatch);
            Assert.AreEqual(DreamPortHintLevel.Error, vm.HintLevel);

            // Hosting reports modHash + modLoader from inspectGameInfo
            vm.McVersionInput = string.Empty;
            vm.ModLoaderInput = string.Empty;
            vm.LocalPortInput = gamePort.ToString();
            await vm.CreateAndHostRoomCommand.ExecuteAsync(null);
            Assert.AreEqual("cafebabe", postedModHash);
            Assert.AreEqual("fabric", postedModLoader);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void CreateTestJar(string jarPath, string entryName, string content)
    {
        using var fs = new FileStream(jarPath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
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

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
    }

    private sealed class LoopbackSlpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[] _responsePacket;

        public LoopbackSlpServer(object statusObject)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _responsePacket = BuildSlpResponsePacket(JsonSerializer.Serialize(statusObject));
            _ = ServeOneAsync();
        }

        public int Port { get; }

        private async Task ServeOneAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                using var stream = client.GetStream();
                var buf = new byte[512];
                _ = await stream.ReadAsync(buf).ConfigureAwait(false);
                await stream.WriteAsync(_responsePacket).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            catch
            {
                // stopped
            }
        }

        private static byte[] BuildSlpResponsePacket(string json)
        {
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            using var body = new MemoryStream();
            body.WriteByte(0x00);
            WriteVarInt(body, jsonBytes.Length);
            body.Write(jsonBytes);
            var bodyArr = body.ToArray();

            using var full = new MemoryStream();
            WriteVarInt(full, bodyArr.Length);
            full.Write(bodyArr);
            return full.ToArray();
        }

        private static void WriteVarInt(Stream dst, int value)
        {
            var u = unchecked((uint)value);
            while ((u & ~0x7Fu) != 0)
            {
                dst.WriteByte((byte)((u & 0x7Fu) | 0x80u));
                u >>= 7;
            }
            dst.WriteByte((byte)u);
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            try { _listener.Dispose(); } catch { }
        }
    }

    private sealed class LoopbackWebSocketServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly CancellationToken _token;
        private readonly ConcurrentQueue<WebSocket> _sockets = new();
        private WebSocket? _latestSocket;
        private int _connectionCount;

        public LoopbackWebSocketServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _token = _cts.Token;
            _ = AcceptLoopAsync();
        }

        public int Port { get; }

        public async Task WaitForConnectionCountAsync(int expectedCount, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (Volatile.Read(ref _connectionCount) >= expectedCount && _latestSocket is { State: WebSocketState.Open })
                    return;
                await Task.Delay(25);
            }
            Assert.IsGreaterThanOrEqualTo(expectedCount, Volatile.Read(ref _connectionCount), "Timed out waiting for WS client");
        }

        public Task BroadcastToLatestAsync(object payload)
            => BroadcastRawJsonToLatestAsync(JsonSerializer.Serialize(payload));

        public async Task BroadcastRawJsonToLatestAsync(string json)
        {
            var sock = _latestSocket ?? throw new InvalidOperationException("No active WebSocket");
            var bytes = Encoding.UTF8.GetBytes(json);
            await sock.SendAsync(bytes, WebSocketMessageType.Text, true, _token).ConfigureAwait(false);
        }

        public async Task DropLatestSocketAsync()
        {
            var sock = Interlocked.Exchange(ref _latestSocket, null);
            if (sock is not null)
            {
                try { sock.Abort(); } catch { }
                try { sock.Dispose(); } catch { }
            }
            await Task.Delay(30);
        }

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
            try
            {
                var stream = tcp.GetStream();
                var reqHeaders = await ReadHttpUpgradeHeadersAsync(stream, _token).ConfigureAwait(false);
                string? key = null;
                foreach (var line in reqHeaders.Split("\r\n"))
                {
                    if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                    {
                        key = line["Sec-WebSocket-Key:".Length..].Trim();
                        break;
                    }
                }
                if (string.IsNullOrEmpty(key))
                {
                    tcp.Dispose();
                    return;
                }

                var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                var handshakeResp =
                    "HTTP/1.1 101 Switching Protocols\r\n" +
                    "Connection: Upgrade\r\n" +
                    "Upgrade: websocket\r\n" +
                    $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(handshakeResp), _token).ConfigureAwait(false);
                await stream.FlushAsync(_token).ConfigureAwait(false);

                using var ws = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(15));
                _sockets.Enqueue(ws);
                Interlocked.Exchange(ref _latestSocket, ws);
                Interlocked.Increment(ref _connectionCount);

                var buf = new byte[4096];
                while (!_token.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    var res = await ws.ReceiveAsync(buf, _token).ConfigureAwait(false);
                    if (res.MessageType == WebSocketMessageType.Close)
                        break;

                    var msg = Encoding.UTF8.GetString(buf, 0, res.Count);
                    if (msg.Contains("\"type\":\"auth\"", StringComparison.Ordinal))
                    {
                        var okBytes = Encoding.UTF8.GetBytes("{\"type\":\"auth_success\",\"serverTimeMs\":1000}");
                        await ws.SendAsync(okBytes, WebSocketMessageType.Text, true, _token).ConfigureAwait(false);
                    }
                    else if (msg.Contains("\"type\":\"ping\"", StringComparison.Ordinal))
                    {
                        var pongBytes = Encoding.UTF8.GetBytes("{\"type\":\"pong\"}");
                        await ws.SendAsync(pongBytes, WebSocketMessageType.Text, true, _token).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                // closed
            }
            finally
            {
                tcp.Dispose();
            }
        }

        private static async Task<string> ReadHttpUpgradeHeadersAsync(NetworkStream stream, CancellationToken ct)
        {
            var ms = new MemoryStream();
            var single = new byte[1];
            var tail = 0;
            while (ms.Length < 16 * 1024)
            {
                var n = await stream.ReadAsync(single, ct).ConfigureAwait(false);
                if (n == 0)
                    break;
                ms.WriteByte(single[0]);
                var b = single[0];
                tail = tail switch
                {
                    0 when b == '\r' => 1,
                    1 when b == '\n' => 2,
                    2 when b == '\r' => 3,
                    3 when b == '\n' => 4,
                    _ => b == '\r' ? 1 : 0
                };
                if (tail == 4)
                    break;
            }
            return Encoding.ASCII.GetString(ms.ToArray());
        }

        public ValueTask DisposeAsync()
        {
            try { _cts.Cancel(); } catch { }
            while (_sockets.TryDequeue(out var ws))
            {
                try { ws.Abort(); } catch { }
                try { ws.Dispose(); } catch { }
            }
            try { _listener.Stop(); } catch { }
            try { _listener.Dispose(); } catch { }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubHttpHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> syncResponder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(syncResponder(request, cancellationToken));
    }

    private sealed class InMemoryVault : IDreamPortSecretVault
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

        public bool TrySaveSecret(string accountKey, string secret, out string? encryptedPayload)
        {
            _secrets[accountKey] = secret;
            encryptedPayload = "mem";
            return true;
        }

        public bool TryLoadSecret(string accountKey, string? encryptedPayload, out string? secret)
            => _secrets.TryGetValue(accountKey, out secret);

        public void DeleteSecret(string accountKey) => _secrets.Remove(accountKey);
    }
}
