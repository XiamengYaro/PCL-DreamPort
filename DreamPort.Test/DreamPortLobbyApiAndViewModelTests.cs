using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using DreamPort.Core.Api;
using DreamPort.Core.Auth;
using DreamPort.Core.Lobby;
using DreamPort.Core.Services;
using DreamPort.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamPort.Test;

[TestClass]
public sealed class DreamPortLobbyApiAndViewModelTests
{
    [TestMethod]
    public async Task ApiClient_LobbyAndFriendsEndpoints_ParsePayloadsAndErrorsCleanly()
    {
        var scenario = "ok";
        using var api = new DreamPortApiClient("https://xmcraft.cn", new StubHttpHandler((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (scenario == "net")
                throw new HttpRequestException("connection reset");

            if (scenario == "expired")
                return JsonResponse(HttpStatusCode.Unauthorized, new { success = false, message = "登录已过期" });

            return path switch
            {
                "/api/lobby/rooms" when req.Method == HttpMethod.Get => JsonResponse(HttpStatusCode.OK, new
                {
                    success = true,
                    data = new
                    {
                        rooms = new object[]
                        {
                            new
                            {
                                code = "ab234567",
                                host = "Xia_Meng_",
                                name = "Test Room",
                                visibility = "public",
                                maxPlayers = 8,
                                mcVersion = "1.21.4",
                                modLoader = "fabric",
                                modHash = "",
                                modList = new[] { "fabric-api" },
                                status = "open",
                                createdAt = 1700000000000L,
                                occupancy = (int?)3,
                                hostGone = false
                            }
                        }
                    }
                }),
                "/api/lobby/rooms/mine" => JsonResponse(HttpStatusCode.OK, new
                {
                    success = true,
                    data = new
                    {
                        room = scenario == "mine-null"
                            ? null
                            : new
                            {
                                code = "MY234567",
                                host = "Alex",
                                name = "Old Room",
                                visibility = "private",
                                maxPlayers = 8,
                                mcVersion = "1.21.4",
                                modLoader = "vanilla",
                                status = "open",
                                createdAt = 1700000000000L
                            }
                    }
                }),
                "/api/lobby/rooms" when req.Method == HttpMethod.Post => JsonResponse(HttpStatusCode.OK, new
                {
                    success = true,
                    message = "房间已创建",
                    data = new
                    {
                        ticket = "ticket-create",
                        ticketTtlSeconds = 120,
                        room = new
                        {
                            code = "NEW23456",
                            host = "Alex",
                            name = "New World",
                            visibility = "public",
                            maxPlayers = 8,
                            mcVersion = "1.21.4",
                            modLoader = "neoforge",
                            status = "open",
                            createdAt = 1700000000000L
                        }
                    }
                }),
                "/api/lobby/rooms/AB234567/join" when scenario == "notfound" => JsonResponse(HttpStatusCode.NotFound, new
                {
                    success = false,
                    message = "房间不存在",
                    data = new { reason = DreamPortRelayWire.ReasonRoomNotFound }
                }),
                "/api/lobby/rooms/AB234567/join" => JsonResponse(HttpStatusCode.OK, new
                {
                    success = true,
                    message = "可以加入",
                    data = new
                    {
                        ticket = "ticket-join",
                        ticketTtlSeconds = 120,
                        diff = new { versionMismatch = true, roomVersion = "1.21.4", myVersion = "1.20.1" },
                        room = new
                        {
                            code = "AB234567",
                            host = "Xia_Meng_",
                            name = "Test Room",
                            visibility = "public",
                            maxPlayers = 8,
                            mcVersion = "1.21.4",
                            modLoader = "fabric",
                            status = "open",
                            createdAt = 1700000000000L,
                            hostGone = false
                        }
                    }
                }),
                "/api/lobby/rooms/AB234567/close" => JsonResponse(HttpStatusCode.OK, new { success = true, message = "房间已关闭" }),
                "/api/lobby/invites" => JsonResponse(HttpStatusCode.OK, new
                {
                    success = true,
                    data = new
                    {
                        ttlSeconds = 1800,
                        incoming = new object[]
                        {
                            new { id = 11L, code = "INV23456", inviter = "Xia_Meng_", invitee = "Alex", message = "来玩", createdAt = 1000L, expiresAt = 2000L, roomOpen = true, roomName = "Friend World", mcVersion = "1.21.4", maxPlayers = 8 }
                        },
                        outgoing = new object[]
                        {
                            new { id = 12L, code = "NEW23456", inviter = "Alex", invitee = "Bob", message = "速来", createdAt = 1000L, expiresAt = 2000L, roomOpen = true }
                        }
                    }
                }),
                "/api/lobby/rooms/NEW23456/invite" => JsonResponse(HttpStatusCode.OK, new { success = true, message = "邀请已发出" }),
                "/api/lobby/invites/11/accept" => JsonResponse(HttpStatusCode.OK, new { success = true, message = "已接受", data = new { code = "inv23456" } }),
                "/api/lobby/invites/11/reject" => JsonResponse(HttpStatusCode.OK, new { success = true, message = "已拒绝" }),
                "/api/lobby/invites/12/cancel" => JsonResponse(HttpStatusCode.OK, new { success = true, message = "已撤回" }),
                "/api/friends" => JsonResponse(HttpStatusCode.OK, new
                {
                    success = true,
                    data = new
                    {
                        friends = new object[]
                        {
                            new { username = "Bob" },
                            new { username = "Charlie" }
                        }
                    }
                }),
                _ => JsonResponse(HttpStatusCode.NotFound, new { success = false, message = $"unhandled {path}" })
            };
        }), disposeHandler: true);

        api.SetToken("tok-lobby-api");

        var rooms = await api.GetLobbyRoomsAsync();
        Assert.IsTrue(rooms.Ok);
        Assert.HasCount(1, rooms.Rooms);
        Assert.AreEqual("AB234567", rooms.Rooms[0].Code);
        Assert.AreEqual(3, rooms.Rooms[0].Occupancy);
        Assert.HasCount(1, rooms.Rooms[0].ModList);

        var mine = await api.GetMyLobbyRoomAsync();
        Assert.IsTrue(mine.Ok);
        Assert.IsNotNull(mine.Room);
        Assert.AreEqual("MY234567", mine.Room.Code);

        scenario = "mine-null";
        var mineEmpty = await api.GetMyLobbyRoomAsync();
        Assert.IsTrue(mineEmpty.Ok);
        Assert.IsNull(mineEmpty.Room);

        scenario = "ok";
        var created = await api.CreateLobbyRoomAsync("New World", "public", "1.21.4", "NeoForge");
        Assert.IsTrue(created.Ok);
        Assert.AreEqual("NEW23456", created.Room!.Code);
        Assert.AreEqual("ticket-create", created.Ticket);
        Assert.AreEqual(120, created.TicketTtlSeconds);

        var joined = await api.JoinLobbyRoomAsync("ab-234567", "1.20.1");
        Assert.IsTrue(joined.Ok);
        Assert.IsNotNull(joined.Diff);
        Assert.IsTrue(joined.Diff.VersionMismatch);
        Assert.AreEqual("ticket-join", joined.Ticket);

        scenario = "notfound";
        var missing = await api.JoinLobbyRoomAsync("AB234567");
        Assert.IsFalse(missing.Ok);
        Assert.AreEqual(DreamPortRelayWire.ReasonRoomNotFound, missing.Reason);

        scenario = "expired";
        var expired = await api.JoinLobbyRoomAsync("AB234567");
        Assert.IsFalse(expired.Ok);
        Assert.AreEqual(DreamPortFailureReason.Expired, expired.FailureReason);

        scenario = "net";
        var netErr = await api.JoinLobbyRoomAsync("AB234567");
        Assert.IsFalse(netErr.Ok);
        Assert.AreEqual(DreamPortFailureReason.Network, netErr.FailureReason);

        scenario = "ok";
        Assert.IsTrue((await api.CloseLobbyRoomAsync("AB234567")).Ok);

        var invites = await api.GetLobbyInvitesAsync();
        Assert.IsTrue(invites.Ok);
        Assert.HasCount(1, invites.Incoming);
        Assert.HasCount(1, invites.Outgoing);
        Assert.AreEqual(1800, invites.TtlSeconds);

        Assert.IsTrue((await api.InviteFriendToRoomAsync("NEW23456", "Bob", "速来")).Ok);
        var acc = await api.AcceptLobbyInviteAsync(11);
        Assert.IsTrue(acc.Ok);
        Assert.AreEqual("INV23456", acc.RoomCode);
        Assert.IsTrue((await api.RejectLobbyInviteAsync(11)).Ok);
        Assert.IsTrue((await api.CancelLobbyInviteAsync(12)).Ok);

        var friends = await api.GetFriendsAsync();
        Assert.IsTrue(friends.Ok);
        CollectionAssert.AreEqual(new[] { "Bob", "Charlie" }, friends.Friends.ToArray());
    }

    [TestMethod]
    public async Task ViewModel_Unauthenticated_CanBrowsePublicRoomsButCannotHostOrJoin()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dp-lobby-anon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new DreamPortSessionStore(tempDir, new InMemoryVault());
            await using var vm = new DreamPortLobbyViewModel(
                store: store,
                clientFactory: url => new DreamPortApiClient(url, new StubHttpHandler((req, _) =>
                {
                    return req.RequestUri!.AbsolutePath switch
                    {
                        "/api/lobby/config" => JsonResponse(HttpStatusCode.OK, new
                        {
                            success = true,
                            data = new { enabled = true, relayHost = "127.0.0.1", relayPort = 17800, relayTls = true, maxPlayersPerRoom = 8 }
                        }),
                        "/api/lobby/rooms" => JsonResponse(HttpStatusCode.OK, new
                        {
                            success = true,
                            data = new
                            {
                                rooms = new object[]
                                {
                                    new
                                    {
                                        code = "PUB23456",
                                        host = "Steve",
                                        name = "Public World",
                                        visibility = "public",
                                        maxPlayers = 8,
                                        mcVersion = "1.21.4",
                                        modLoader = "vanilla",
                                        status = "open",
                                        createdAt = 1700000000000L
                                    }
                                }
                            }
                        }),
                        _ => JsonResponse(HttpStatusCode.Unauthorized, new { success = false })
                    };
                }), disposeHandler: true));

            await vm.InitializeOrSyncAsync();

            Assert.IsFalse(vm.IsLoggedIn);
            Assert.IsFalse(vm.IsRelayEnabled);
            Assert.IsFalse(vm.CanHostOrJoin);
            Assert.IsTrue(vm.HasPublicRooms);
            Assert.HasCount(1, vm.PublicRooms);
            StringAssert.Contains(vm.PublicRooms[0].VersionSummary, "原版");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ViewModel_GuestVersionMismatch_InviteGuardsAndRejectAcceptFlow()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dp-lobby-guest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        await using var relay = new MinimalSimulatedRelay();
        try
        {
            var store = new DreamPortSessionStore(tempDir, new InMemoryVault());
            store.Save("https://xmcraft.cn", "Alex", false, "tok-vm-guest");

            var hasStale = true;
            var acceptCalls = 0;
            var rejectCalls = 0;
            var outgoingInvites = new List<object>();
            var incomingInvites = new List<object>
            {
                new { id = 21L, code = "RMN23456", inviter = "Xia_Meng_", invitee = "Alex", message = "进房", createdAt = 1000L, expiresAt = 2000L, roomOpen = true, roomName = "Coop", mcVersion = "1.21.4", maxPlayers = 8 },
                new { id = 22L, code = "RMN34567", inviter = "Bob", invitee = "Alex", message = "来", createdAt = 1000L, expiresAt = 2000L, roomOpen = true, roomName = "Bob World", mcVersion = "1.21.4", maxPlayers = 8 }
            };

            await using var vm = new DreamPortLobbyViewModel(
                store: store,
                clientFactory: url => new DreamPortApiClient(url, new StubHttpHandler((req, _) =>
                {
                    var path = req.RequestUri!.AbsolutePath;
                    return path switch
                    {
                        "/api/lobby/config" => JsonResponse(HttpStatusCode.OK, new
                        {
                            success = true,
                            data = new { enabled = true, relayHost = "127.0.0.1", relayPort = relay.Port, relayTls = false, maxPlayersPerRoom = 8 }
                        }),
                        "/api/lobby/rooms" => JsonResponse(HttpStatusCode.OK, new { success = true, data = new { rooms = Array.Empty<object>() } }),
                        "/api/lobby/rooms/mine" => JsonResponse(HttpStatusCode.OK, new
                        {
                            success = true,
                            data = new
                            {
                                room = hasStale
                                    ? new { code = "STA23456", host = "Alex", name = "Stale", visibility = "public", maxPlayers = 8, status = "open", createdAt = 1000L }
                                    : null
                            }
                        }),
                        "/api/lobby/rooms/STA23456/close" => HandleCloseStale(ref hasStale),
                        "/api/lobby/invites" => JsonResponse(HttpStatusCode.OK, new
                        {
                            success = true,
                            data = new { ttlSeconds = 1800, incoming = incomingInvites, outgoing = outgoingInvites }
                        }),
                        "/api/friends" => JsonResponse(HttpStatusCode.OK, new
                        {
                            success = true,
                            data = new { friends = new[] { new { username = "Bob" } } }
                        }),
                        "/api/lobby/rooms/RMG23456/join" => JsonResponse(HttpStatusCode.OK, new
                        {
                            success = true,
                            data = new
                            {
                                ticket = "t-join-mismatch",
                                ticketTtlSeconds = 120,
                                diff = new { versionMismatch = true, roomVersion = "1.21.4", myVersion = "1.20.1" },
                                room = new { code = "RMG23456", host = "Xia_Meng_", name = "Mismatch World", visibility = "public", maxPlayers = 8, mcVersion = "1.21.4", modLoader = "vanilla", status = "open", createdAt = 1000L, hostGone = false }
                            }
                        }),
                        "/api/lobby/invites/21/accept" => HandleAccept21(ref acceptCalls),
                        "/api/lobby/invites/22/reject" => HandleReject22(ref rejectCalls, incomingInvites),
                        "/api/lobby/rooms/RMN23456/join" => JsonResponse(HttpStatusCode.OK, new
                        {
                            success = true,
                            data = new
                            {
                                ticket = "t-join-inv",
                                ticketTtlSeconds = 120,
                                room = new { code = "RMN23456", host = "Xia_Meng_", name = "Coop", visibility = "private", maxPlayers = 8, mcVersion = "1.21.4", modLoader = "vanilla", status = "open", createdAt = 1000L, hostGone = false }
                            }
                        }),
                        _ => JsonResponse(HttpStatusCode.NotFound, new { success = false })
                    };
                }), disposeHandler: true),
                getSelectedInstanceInfo: () => ("1.20.1", "fabric"));

            await vm.InitializeOrSyncAsync();

            // ① Stale room self-heal
            Assert.IsTrue(vm.HasStaleRoom);
            Assert.AreEqual("STA23456", vm.StaleRoomCode);
            await vm.CloseStaleRoomCommand.ExecuteAsync(null);
            Assert.IsFalse(vm.HasStaleRoom);

            // ② Version mismatch -> cancel -> retry -> confirm join -> active guest session
            vm.JoinRoomCodeInput = "RMG23456";
            await vm.JoinByCodeCommand.ExecuteAsync(null);
            Assert.IsTrue(vm.HasPendingVersionMismatch, vm.StatusHint);
            Assert.IsFalse(vm.IsGuest);

            vm.CancelForceJoinCommand.Execute(null);
            Assert.IsFalse(vm.HasPendingVersionMismatch);

            await vm.JoinByCodeCommand.ExecuteAsync(null);
            Assert.IsTrue(vm.HasPendingVersionMismatch);
            await vm.ConfirmForceJoinCommand.ExecuteAsync(null);
            Assert.IsFalse(vm.HasPendingVersionMismatch);
            Assert.IsTrue(vm.IsGuest);
            StringAssert.StartsWith(vm.GuestLocalAddress, "127.0.0.1:");

            // ③ Guard blocks accepting incoming invite while already in an active room (NO HTTP POST sent)
            Assert.HasCount(2, vm.IncomingInvites);
            await ((IAsyncRelayCommand)vm.IncomingInvites[0].AcceptCommand!).ExecuteAsync(null);
            Assert.AreEqual(0, acceptCalls);
            Assert.AreEqual(DreamPortHintLevel.Warning, vm.HintLevel);

            // ④ Leave active room -> reject second invite -> accept first invite cleanly
            await vm.LeaveRoomCommand.ExecuteAsync(null);
            Assert.IsFalse(vm.IsGuest);

            await ((IAsyncRelayCommand)vm.IncomingInvites[1].RejectCommand!).ExecuteAsync(null);
            Assert.AreEqual(1, rejectCalls);
            Assert.HasCount(1, vm.IncomingInvites);

            await ((IAsyncRelayCommand)vm.IncomingInvites[0].AcceptCommand!).ExecuteAsync(null);
            Assert.AreEqual(1, acceptCalls);
            Assert.IsTrue(vm.IsGuest);
            Assert.AreEqual("RMN23456", vm.ActiveRoomCode);

            await vm.LeaveRoomCommand.ExecuteAsync(null);
            Assert.IsFalse(vm.IsGuest);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }

        static HttpResponseMessage HandleCloseStale(ref bool staleFlag)
        {
            staleFlag = false;
            return JsonResponse(HttpStatusCode.OK, new { success = true, message = "已关闭残留房间" });
        }

        static HttpResponseMessage HandleAccept21(ref int count)
        {
            count++;
            return JsonResponse(HttpStatusCode.OK, new { success = true, message = "已接受", data = new { code = "RMN23456" } });
        }

        static HttpResponseMessage HandleReject22(ref int count, List<object> incoming)
        {
            count++;
            incoming.RemoveAt(1);
            return JsonResponse(HttpStatusCode.OK, new { success = true, message = "已拒绝" });
        }
    }

    [TestMethod]
    public async Task ViewModel_HostLifecycle_CreatesInvitesCancelsManagesMembersAndStops()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dp-lobby-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        using var localGameListener = new TcpListener(IPAddress.Loopback, 0);
        localGameListener.Start();
        var gamePort = ((IPEndPoint)localGameListener.LocalEndpoint).Port;

        await using var relay = new MinimalSimulatedRelay();
        try
        {
            var store = new DreamPortSessionStore(tempDir, new InMemoryVault());
            store.Save("https://xmcraft.cn", "Xia_Meng_", false, "tok-vm-host");

            var serverRoomClosed = false;
            var outgoingInvites = new List<object>();

            await using var vm = new DreamPortLobbyViewModel(
                store: store,
                clientFactory: url => new DreamPortApiClient(url, new StubHttpHandler((req, _) =>
                {
                    var path = req.RequestUri!.AbsolutePath;
                    return path switch
                    {
                        "/api/lobby/config" => JsonResponse(HttpStatusCode.OK, new
                        {
                            success = true,
                            data = new { enabled = true, relayHost = "127.0.0.1", relayPort = relay.Port, relayTls = false, maxPlayersPerRoom = 8 }
                        }),
                        "/api/lobby/rooms" when req.Method == HttpMethod.Get => JsonResponse(HttpStatusCode.OK, new { success = true, data = new { rooms = Array.Empty<object>() } }),
                        "/api/lobby/rooms/mine" => JsonResponse(HttpStatusCode.OK, new { success = true, data = new { room = (object?)null } }),
                        "/api/lobby/invites" => JsonResponse(HttpStatusCode.OK, new
                        {
                            success = true,
                            data = new { ttlSeconds = 1800, incoming = Array.Empty<object>(), outgoing = outgoingInvites }
                        }),
                        "/api/friends" => JsonResponse(HttpStatusCode.OK, new
                        {
                            success = true,
                            data = new { friends = new[] { new { username = "Alex" } } }
                        }),
                        "/api/lobby/rooms" when req.Method == HttpMethod.Post => JsonResponse(HttpStatusCode.OK, new
                        {
                            success = true,
                            data = new
                            {
                                ticket = "t-host-live",
                                ticketTtlSeconds = 120,
                                room = new { code = "HOST2345", host = "Xia_Meng_", name = "Host Room", visibility = "public", maxPlayers = 8, mcVersion = "1.21.4", modLoader = "fabric", status = "open", createdAt = 1000L }
                            }
                        }),
                        "/api/lobby/rooms/HOST2345/invite" => HandleSendInvite(outgoingInvites),
                        "/api/lobby/invites/99/cancel" => HandleCancelInvite(outgoingInvites),
                        "/api/lobby/rooms/HOST2345/close" => HandleCloseRoom(ref serverRoomClosed),
                        _ => JsonResponse(HttpStatusCode.NotFound, new { success = false })
                    };
                }), disposeHandler: true),
                getSelectedInstanceInfo: () => ("1.21.4", "fabric"));

            await vm.InitializeOrSyncAsync();

            Assert.AreEqual("1.21.4", vm.McVersionInput);
            Assert.AreEqual("fabric", vm.ModLoaderInput);
            Assert.IsTrue(vm.HasFriendOptions);

            vm.RoomNameInput = "Host Room";
            vm.LocalPortInput = gamePort.ToString();
            await vm.CreateAndHostRoomCommand.ExecuteAsync(null);

            Assert.IsTrue(vm.IsHosting);
            Assert.AreEqual("HOST2345", vm.ActiveRoomCode);
            await relay.WaitForHostControlAsync(TimeSpan.FromSeconds(5));

            // Push member_list with host + guest Alex
            await relay.SendToHostControlAsync(DreamPortRelayWire.TypeMemberList, new
            {
                members = new object[]
                {
                    new { peerId = "host", playerName = "Xia_Meng_", isHost = true, kicked = false, joinedAt = 1000L },
                    new { peerId = "peer-alex", playerName = "Alex", isHost = false, kicked = false, joinedAt = 2000L }
                }
            });

            Assert.IsTrue(await WaitUntilAsync(() => vm.Members.Count == 2, TimeSpan.FromSeconds(3)));
            var guestMember = vm.Members.First(m => m.PlayerName == "Alex");
            await ((IAsyncRelayCommand)guestMember.KickCommand).ExecuteAsync(null);
            Assert.IsTrue(await WaitUntilAsync(() => relay.KickTargets.Contains("Alex"), TimeSpan.FromSeconds(3)));

            await ((IAsyncRelayCommand)guestMember.UnkickCommand).ExecuteAsync(null);
            Assert.IsTrue(await WaitUntilAsync(() => relay.UnkickTargets.Contains("Alex"), TimeSpan.FromSeconds(3)));

            // Send and cancel friend invite
            vm.InviteFriendUsernameInput = "Alex";
            vm.InviteMessageInput = "一起玩";
            await vm.SendFriendInviteCommand.ExecuteAsync(null);
            Assert.HasCount(1, vm.OutgoingInvites);

            await ((IAsyncRelayCommand)vm.OutgoingInvites[0].CancelCommand!).ExecuteAsync(null);
            Assert.HasCount(0, vm.OutgoingInvites);

            // Stop hosting closes room cleanly
            await vm.StopHostingCommand.ExecuteAsync(null);
            Assert.IsFalse(vm.IsHosting);
            Assert.IsTrue(serverRoomClosed);
            Assert.AreEqual(string.Empty, vm.ActiveRoomCode);
            Assert.HasCount(0, vm.Members);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }

        static HttpResponseMessage HandleSendInvite(List<object> outgoing)
        {
            outgoing.Add(new { id = 99L, code = "HOST2345", inviter = "Xia_Meng_", invitee = "Alex", message = "一起玩", createdAt = 1000L, expiresAt = 2000L, roomOpen = true });
            return JsonResponse(HttpStatusCode.OK, new { success = true, message = "已发送邀请" });
        }

        static HttpResponseMessage HandleCancelInvite(List<object> outgoing)
        {
            outgoing.Clear();
            return JsonResponse(HttpStatusCode.OK, new { success = true, message = "已撤回邀请" });
        }

        static HttpResponseMessage HandleCloseRoom(ref bool closedFlag)
        {
            closedFlag = true;
            return JsonResponse(HttpStatusCode.OK, new { success = true, message = "房间已关闭" });
        }
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

    private sealed class MinimalSimulatedRelay : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly CancellationToken _token;
        private readonly TaskCompletionSource _hostReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _hostWriteLock = new(1, 1);
        private NetworkStream? _hostStream;

        public MinimalSimulatedRelay()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _token = _cts.Token;
            _ = AcceptLoopAsync();
        }

        public int Port { get; }
        public ConcurrentQueue<string> KickTargets { get; } = new();
        public ConcurrentQueue<string> UnkickTargets { get; } = new();

        public Task WaitForHostControlAsync(TimeSpan timeout) => _hostReady.Task.WaitAsync(timeout);

        public async Task SendToHostControlAsync(string type, object? body = null)
        {
            var stream = _hostStream ?? throw new InvalidOperationException("host-control not ready");
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

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_token.IsCancellationRequested)
                {
                    var tcp = await _listener.AcceptTcpClientAsync(_token).ConfigureAwait(false);
                    _ = HandleAsync(tcp);
                }
            }
            catch
            {
                // stopped
            }
        }

        private async Task HandleAsync(TcpClient tcp)
        {
            using (tcp)
            {
                try
                {
                    var stream = tcp.GetStream();
                    using var hello = await DreamPortRelayWire.ReadFrameAsync(stream, _token).ConfigureAwait(false);
                    if (hello is null)
                        return;

                    var role = hello.Body.GetProperty("role").GetString() ?? string.Empty;
                    if (role == DreamPortRelayWire.RoleHostControl)
                    {
                        _hostStream = stream;
                        await SendToHostControlAsync(DreamPortRelayWire.TypeHelloAck, new { protocol = 1, peerId = "host" }).ConfigureAwait(false);
                        _hostReady.TrySetResult();
                        while (!_token.IsCancellationRequested)
                        {
                            using var f = await DreamPortRelayWire.ReadFrameAsync(stream, _token).ConfigureAwait(false);
                            if (f is null)
                                break;
                            if (f.Type == DreamPortRelayWire.TypePing)
                                await SendToHostControlAsync(DreamPortRelayWire.TypePong).ConfigureAwait(false);
                            else if (f.Type == DreamPortRelayWire.TypeKickMember && f.Body.TryGetProperty("playerName", out var kp))
                                KickTargets.Enqueue(kp.GetString() ?? string.Empty);
                            else if (f.Type == DreamPortRelayWire.TypeUnkickMember && f.Body.TryGetProperty("playerName", out var up))
                                UnkickTargets.Enqueue(up.GetString() ?? string.Empty);
                        }
                    }
                    else if (role == DreamPortRelayWire.RoleGuestControl)
                    {
                        await DreamPortRelayWire.WriteFrameAsync(stream, DreamPortRelayWire.TypeHelloAck, new { protocol = 1, peerId = "peer-1" }, _token).ConfigureAwait(false);
                        while (!_token.IsCancellationRequested)
                        {
                            using var f = await DreamPortRelayWire.ReadFrameAsync(stream, _token).ConfigureAwait(false);
                            if (f is null)
                                break;
                            if (f.Type == DreamPortRelayWire.TypePing)
                                await DreamPortRelayWire.WriteFrameAsync(stream, DreamPortRelayWire.TypePong, null, _token).ConfigureAwait(false);
                        }
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
