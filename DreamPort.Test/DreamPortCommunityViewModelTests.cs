using System.Net;
using System.Text;
using System.Text.Json;
using DreamPort.Core.Api;
using DreamPort.Core.Auth;
using DreamPort.Core.Services;
using DreamPort.UI.Services;
using DreamPort.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamPort.Test;

[TestClass]
public sealed class DreamPortCommunityViewModelTests
{
    [TestMethod]
    public async Task UnauthenticatedUser_LoadsPublicAnnouncements_AndResetsUnread()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dp-comm-anon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            DreamPortShellBridge.PublishUnreadCount(9);
            var store = new DreamPortSessionStore(tempDir, new InMemoryVault());
            var navigatedToAccount = false;

            using var vm = new DreamPortCommunityViewModel(
                store: store,
                clientFactory: url => new DreamPortApiClient(url, new StubHttpHandler((req, _) =>
                {
                    if (req.RequestUri!.AbsolutePath == "/api/announcements")
                    {
                        return JsonResponse(HttpStatusCode.OK, new
                        {
                            success = true,
                            data = new
                            {
                                news = new[]
                                {
                                    new { title = "公开公告 A", content = "正文内容", date = "2026-11-25", pinned = true }
                                }
                            }
                        });
                    }

                    return JsonResponse(HttpStatusCode.Unauthorized, new { success = false });
                }), disposeHandler: true),
                openUrl: _ => { },
                requestGoToAccount: () => navigatedToAccount = true);

            await vm.InitializeOrSyncAsync();

            Assert.IsFalse(vm.IsLoggedIn);
            Assert.IsTrue(vm.IsNotLoggedIn);
            Assert.HasCount(1, vm.Announcements);
            Assert.HasCount(0, vm.VisibleNotifications);
            Assert.AreEqual(0, vm.UnreadCount);
            Assert.AreEqual(0, DreamPortShellBridge.UnreadNotificationCount);

            vm.GoToAccountPageCommand.Execute(null);
            Assert.IsTrue(navigatedToAccount);
        }
        finally
        {
            DreamPortShellBridge.ResetUnreadCount();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task LoggedInUser_FiltersUnread_MarksSingleAndAllRead_AndUpdatesShellBridge()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dp-comm-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new DreamPortSessionStore(tempDir, new InMemoryVault());
            store.Save("https://xmcraft.cn", "Alex", false, "tok-comm");
            var openedUrls = new List<string>();
            var unread = 2;

            using var vm = new DreamPortCommunityViewModel(
                store: store,
                clientFactory: url => new DreamPortApiClient(url, new StubHttpHandler((req, _) =>
                {
                    return req.RequestUri!.AbsolutePath switch
                    {
                        "/api/announcements" => JsonResponse(HttpStatusCode.OK, new { success = true, data = new { news = Array.Empty<object>() } }),
                        "/api/notifications" => JsonResponse(HttpStatusCode.OK, new
                        {
                            success = true,
                            data = new
                            {
                                items = new object[]
                                {
                                    new { id = 1L, type = "approve", title = "通知 1", body = "详情 1", link = "/tasks", read = false, createdAt = 1764028800000L },
                                    new { id = 2L, type = "system", title = "通知 2", body = "详情 2", link = "/announcements", read = false, createdAt = 1764028700000L },
                                    new { id = 3L, type = "system", title = "通知 3", body = "详情 3", link = "", read = true, createdAt = 1764028600000L }
                                },
                                total = 3,
                                unread,
                                page = 1,
                                pageSize = 30
                            }
                        }),
                        "/api/notifications/1/read" => OkRead(++unread - 2, out unread),
                        "/api/notifications/read-all" => OkReadAll(out unread),
                        _ => JsonResponse(HttpStatusCode.NotFound, new { success = false })
                    };

                    static HttpResponseMessage OkRead(int _, out int nextUnread)
                    {
                        nextUnread = 1;
                        return JsonResponse(HttpStatusCode.OK, new { success = true, data = new { unread = 1 } });
                    }

                    static HttpResponseMessage OkReadAll(out int nextUnread)
                    {
                        nextUnread = 0;
                        return JsonResponse(HttpStatusCode.OK, new { success = true, data = new { marked = 1, unread = 0 } });
                    }
                }), disposeHandler: true),
                openUrl: url => openedUrls.Add(url));

            await vm.InitializeOrSyncAsync();
            Assert.IsTrue(vm.IsLoggedIn);
            Assert.AreEqual(2, vm.UnreadCount);
            Assert.AreEqual(2, DreamPortShellBridge.UnreadNotificationCount);
            Assert.HasCount(3, vm.VisibleNotifications);

            // 筛选仅未读
            vm.ShowUnreadOnlyChecked = true;
            Assert.HasCount(2, vm.VisibleNotifications);

            // 点击第一条的查看详情 -> 打开链接并标为已读
            await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.VisibleNotifications[0].OpenLinkCommand!).ExecuteAsync(null);
            Assert.HasCount(1, openedUrls);
            Assert.AreEqual("https://xmcraft.cn/tasks", openedUrls[0]);
            Assert.AreEqual(1, vm.UnreadCount);
            Assert.AreEqual(1, DreamPortShellBridge.UnreadNotificationCount);
            Assert.HasCount(1, vm.VisibleNotifications);

            // 全部标为已读
            await vm.MarkAllReadCommand.ExecuteAsync(null);
            Assert.AreEqual(0, vm.UnreadCount);
            Assert.AreEqual(0, DreamPortShellBridge.UnreadNotificationCount);
            Assert.HasCount(0, vm.VisibleNotifications);
        }
        finally
        {
            DreamPortShellBridge.ResetUnreadCount();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task AccountViewModel_AutoSignin_Points_TitlesAndShellBridgeReset()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dp-acct-p3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new DreamPortSessionStore(tempDir, new InMemoryVault());
            store.Save("https://xmcraft.cn", "Alex", false, "tok-p3");
            var signedToday = false;
            var points = 100;
            var streak = 4;
            string? equipped = "pioneer";

            using var vm = new DreamPortAccountViewModel(
                store: store,
                clientFactory: url => new DreamPortApiClient(url, new StubHttpHandler((req, _) =>
                {
                    var path = req.RequestUri!.AbsolutePath;
                    return path switch
                    {
                        "/api/auth/validate" => JsonResponse(HttpStatusCode.OK, new { success = true, data = new { valid = true, username = "Alex", isAdmin = false } }),
                        "/api/user/profile" => JsonResponse(HttpStatusCode.OK, new { success = true, data = new { username = "Alex", status = "approved", minecraftName = "AlexMC", points } }),
                        "/api/user/progress" => JsonResponse(HttpStatusCode.OK, new
                        {
                            success = true,
                            data = new { currentStep = "complete", doneCount = 6, totalCount = 6, gated = false, showBar = false, needsAccountVerification = false, qqBindMode = "required", rejected = false, banned = false, bindHint = false, steps = Array.Empty<object>() }
                        }),
                        "/api/auth/sessions" => JsonResponse(HttpStatusCode.OK, new { success = true, data = Array.Empty<object>() }),
                        "/api/notifications/unread" => JsonResponse(HttpStatusCode.OK, new { success = true, data = new { unread = 3 } }),
                        "/api/points/center" => JsonResponse(HttpStatusCode.OK, new
                        {
                            success = true,
                            data = new
                            {
                                balance = points,
                                signin = new { today = signedToday, streak, monthDays = 10 },
                                tasks = new { daily = new[] { new { id = "t1", completed = true, claimed = false } } }
                            }
                        }),
                        "/api/points/signin" => DoSignin(),
                        "/api/titles/mine" => JsonResponse(HttpStatusCode.OK, new
                        {
                            success = true,
                            data = new
                            {
                                equipped,
                                owned = new[]
                                {
                                    new { code = "pioneer", name = "先行者", desc = "开服", color = "#FFF", obtainedAt = 1700000000000L },
                                    new { code = "builder", name = "建筑大师", desc = "建筑", color = "#38BDF8", obtainedAt = 1700000001000L }
                                },
                                locked = Array.Empty<object>(),
                                achievements = new[] { new { id = "a1", completed = true } }
                            }
                        }),
                        "/api/titles/equip" => DoEquip(req),
                        "/api/titles/unequip" => DoUnequip(),
                        "/api/logout" => JsonResponse(HttpStatusCode.OK, new { success = true }),
                        _ => JsonResponse(HttpStatusCode.NotFound, new { success = false })
                    };

                    HttpResponseMessage DoSignin()
                    {
                        signedToday = true;
                        points += 15;
                        streak += 1;
                        return JsonResponse(HttpStatusCode.OK, new
                        {
                            success = true,
                            message = "签到成功",
                            data = new { first = true, alreadySigned = false, message = "签到成功", signedDate = "2026-11-25", streak, monthDays = 11, total = 30, pointsAwarded = 15 }
                        });
                    }

                    HttpResponseMessage DoEquip(HttpRequestMessage r)
                    {
                        var body = r.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
                        equipped = body.Contains("builder", StringComparison.Ordinal) ? "builder" : "pioneer";
                        return JsonResponse(HttpStatusCode.OK, new { success = true, message = "已佩戴" });
                    }

                    HttpResponseMessage DoUnequip()
                    {
                        equipped = null;
                        return JsonResponse(HttpStatusCode.OK, new { success = true, message = "已脱下" });
                    }
                }), disposeHandler: true),
                openUrl: _ => { },
                autoSigninService: new DreamPortAutoSigninService(tempDir, () => new DateOnly(2026, 11, 25)));

            await vm.InitializeAsync();
            Assert.IsTrue(vm.IsSignedToday);
            Assert.AreEqual("115 积分", vm.PointsBalanceText);
            Assert.AreEqual(3, DreamPortShellBridge.UnreadNotificationCount);
            Assert.IsTrue(vm.HasOwnedTitles);
            Assert.HasCount(2, vm.OwnedTitles);
            StringAssert.Contains(vm.EquippedTitleText, "先行者");

            // 切换佩戴第二个称号
            vm.SelectedTitleOption = vm.OwnedTitles[1];
            await vm.ApplySelectedTitleCommand.ExecuteAsync(null);
            StringAssert.Contains(vm.EquippedTitleText, "建筑大师");
            Assert.IsTrue(vm.CanUnequipTitle);

            // 卸下称号
            await vm.UnequipTitleCommand.ExecuteAsync(null);
            StringAssert.Contains(vm.EquippedTitleText, "未佩戴称号");
            Assert.IsFalse(vm.CanUnequipTitle);

            // 退出登录清零未读角标
            await vm.LogoutCommand.ExecuteAsync(null);
            Assert.AreEqual(0, DreamPortShellBridge.UnreadNotificationCount);
            Assert.IsFalse(vm.ShowRewardsCard);
        }
        finally
        {
            DreamPortShellBridge.ResetUnreadCount();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
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
