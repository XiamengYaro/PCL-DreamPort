using System.Net;
using System.Text;
using System.Text.Json;
using DreamPort.Core.Api;
using DreamPort.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamPort.Test;

[TestClass]
public sealed class DreamPortPhase3ApiAndSigninTests
{
    [TestMethod]
    public async Task PointsCenterAndSignin_HandlesRewardAlreadySignedForbiddenExpiredAndNetwork()
    {
        var scenario = "ok";
        using var api = new DreamPortApiClient("https://xmcraft.cn", new StubHttpHandler((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (scenario == "net")
                throw new HttpRequestException("connection reset");

            if (path == "/api/points/center")
            {
                if (scenario == "expired")
                    return JsonResponse(HttpStatusCode.Unauthorized, new { success = false, message = "登录已过期" });

                return JsonResponse(HttpStatusCode.OK, new
                {
                    success = true,
                    data = new
                    {
                        balance = 260,
                        signin = new { today = false, streak = 5, monthDays = 14 },
                        tasks = new
                        {
                            daily = new object[]
                            {
                                new { id = "t1", completed = true, claimed = false },
                                new { id = "t2", completed = true, claimed = true }
                            },
                            weekly = new object[]
                            {
                                new { id = "w1", completed = true, claimed = false },
                                new { id = "w2", completed = false, claimed = false }
                            }
                        }
                    }
                });
            }

            if (path == "/api/points/signin")
            {
                return scenario switch
                {
                    "first" => JsonResponse(HttpStatusCode.OK, new
                    {
                        success = true,
                        message = "签到成功",
                        data = new
                        {
                            first = true,
                            alreadySigned = false,
                            message = "签到成功",
                            signedDate = "2026-11-25",
                            streak = 6,
                            monthDays = 15,
                            total = 48,
                            pointsAwarded = 15,
                            rewards = new[] { "15 积分", "强化秘药 1 瓶" }
                        }
                    }),
                    "already" => JsonResponse(HttpStatusCode.BadRequest, new { success = false, message = "今日已签到" }),
                    "forbidden" => JsonResponse(HttpStatusCode.Forbidden, new { success = false, message = "请先完成入服白名单审核" }),
                    "expired" => JsonResponse(HttpStatusCode.Unauthorized, new { success = false, message = "登录已失效" }),
                    _ => JsonResponse(HttpStatusCode.NotFound, new { success = false })
                };
            }

            return JsonResponse(HttpStatusCode.NotFound, new { success = false });
        }), disposeHandler: true);

        api.SetToken("tok-1");

        var center = await api.GetPointsCenterAsync();
        Assert.IsTrue(center.Ok);
        Assert.AreEqual(260, center.Balance);
        Assert.IsFalse(center.SignedToday);
        Assert.AreEqual(5, center.Streak);
        Assert.AreEqual(14, center.MonthDays);
        Assert.AreEqual(2, center.PendingClaimCount);

        scenario = "first";
        var sFirst = await api.SigninAsync();
        Assert.IsTrue(sFirst.Ok);
        Assert.IsTrue(sFirst.First);
        Assert.AreEqual(15, sFirst.PointsAwarded);
        Assert.AreEqual(6, sFirst.Streak);
        Assert.HasCount(2, sFirst.Rewards);

        scenario = "already";
        var sAlready = await api.SigninAsync();
        Assert.IsTrue(sAlready.Ok);
        Assert.IsFalse(sAlready.First);
        Assert.IsTrue(sAlready.AlreadySigned);

        scenario = "forbidden";
        var sForbid = await api.SigninAsync();
        Assert.IsFalse(sForbid.Ok);
        Assert.IsNull(sForbid.FailureReason);
        StringAssert.Contains(sForbid.Message, "白名单");

        scenario = "expired";
        var sExp = await api.SigninAsync();
        Assert.IsFalse(sExp.Ok);
        Assert.AreEqual(DreamPortFailureReason.Expired, sExp.FailureReason);

        var cExp = await api.GetPointsCenterAsync();
        Assert.IsFalse(cExp.Ok);
        Assert.AreEqual(DreamPortFailureReason.Expired, cExp.FailureReason);

        scenario = "net";
        var sNet = await api.SigninAsync();
        Assert.IsFalse(sNet.Ok);
        Assert.AreEqual(DreamPortFailureReason.Network, sNet.FailureReason);
    }

    [TestMethod]
    public async Task TitlesAndAnnouncementsAndNotifications_ParseAllFieldsCleanly()
    {
        var equipped = "pioneer";
        var unreadCount = 2;
        using var api = new DreamPortApiClient("https://xmcraft.cn", new StubHttpHandler(async (req, ct) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);

            switch (path)
            {
                case "/api/titles/mine":
                    return JsonResponse(HttpStatusCode.OK, new
                    {
                        success = true,
                        data = new
                        {
                            equipped,
                            owned = new[]
                            {
                                new { code = "pioneer", name = "先行者", desc = "开服元老", color = "#F59E0B", obtainedAt = 1700000000000L },
                                new { code = "builder", name = "建筑大师", desc = "精工细作", color = "#38BDF8", obtainedAt = 1700000001000L }
                            },
                            locked = new[] { new { code = "hidden" } },
                            achievements = new[]
                            {
                                new { id = "a1", completed = true },
                                new { id = "a2", completed = false }
                            }
                        }
                    });
                case "/api/titles/equip":
                    StringAssert.Contains(body, "builder");
                    equipped = "builder";
                    return JsonResponse(HttpStatusCode.OK, new { success = true, message = "已佩戴" });
                case "/api/titles/unequip":
                    equipped = "";
                    return JsonResponse(HttpStatusCode.OK, new { success = true, message = "已脱下" });
                case "/api/announcements":
                    return JsonResponse(HttpStatusCode.OK, new
                    {
                        success = true,
                        data = new
                        {
                            news = new object[]
                            {
                                new
                                {
                                    title = "冬日维护公告",
                                    content = "## 更新内容\n- **修复：资源同步**\n点击 [官网详情](https://xmcraft.cn/news) 查看",
                                    date = "",
                                    publishAt = 1764028800000L,
                                    tag = "published",
                                    pinned = true
                                },
                                new
                                {
                                    name = "活动预告",
                                    subtitle = "本周周末双倍积分",
                                    createdAt = "2026-11-24T10:00:00Z",
                                    category = "活动",
                                    level = "important"
                                }
                            }
                        }
                    });
                case "/api/notifications":
                    return JsonResponse(HttpStatusCode.OK, new
                    {
                        success = true,
                        data = new
                        {
                            items = new object[]
                            {
                                new { id = 11L, type = "approve", title = "白名单通过", body = "欢迎入服", link = "/tasks", read = false, createdAt = 1764028800000L },
                                new { id = 12L, type = "security", title = "安全提醒", body = "测试", link = "javascript:alert(1)", read = false, createdAt = 1764028900000L }
                            },
                            total = 2,
                            unread = unreadCount,
                            page = 1,
                            pageSize = 30
                        }
                    });
                case "/api/notifications/unread":
                    return JsonResponse(HttpStatusCode.OK, new { success = true, data = new { unread = unreadCount } });
                case "/api/notifications/11/read":
                    unreadCount = 1;
                    return JsonResponse(HttpStatusCode.OK, new { success = true, data = new { unread = unreadCount } });
                case "/api/notifications/read-all":
                    unreadCount = 0;
                    return JsonResponse(HttpStatusCode.OK, new { success = true, data = new { marked = 1, unread = 0 } });
                default:
                    return JsonResponse(HttpStatusCode.NotFound, new { success = false });
            }
        }), disposeHandler: true);

        api.SetToken("tok-title");

        var titles = await api.GetMyTitlesAsync();
        Assert.IsTrue(titles.Ok);
        Assert.AreEqual("pioneer", titles.EquippedCode);
        Assert.HasCount(2, titles.Owned);
        Assert.AreEqual(1, titles.LockedCount);
        Assert.AreEqual(1, titles.CompletedAchievements);
        Assert.AreEqual(2, titles.TotalAchievements);

        var equipRes = await api.EquipTitleAsync("builder");
        Assert.IsTrue(equipRes.Ok);
        var unequipRes = await api.UnequipTitleAsync();
        Assert.IsTrue(unequipRes.Ok);

        var ann = await api.GetAnnouncementsAsync();
        Assert.IsTrue(ann.Ok);
        Assert.HasCount(2, ann.Items);
        Assert.AreEqual("冬日维护公告", ann.Items[0].Title);
        Assert.IsTrue(ann.Items[0].Important);
        Assert.IsNull(ann.Items[0].Tag);
        Assert.IsFalse(string.IsNullOrWhiteSpace(ann.Items[0].DateText));
        Assert.IsFalse(ann.Items[0].Body.Contains("**", StringComparison.Ordinal));
        Assert.IsFalse(ann.Items[0].Body.Contains("##", StringComparison.Ordinal));
        StringAssert.Contains(ann.Items[0].Body, "官网详情");

        Assert.AreEqual("活动预告", ann.Items[1].Title);
        Assert.AreEqual("活动", ann.Items[1].Tag);
        Assert.IsTrue(ann.Items[1].Important);

        var notifs = await api.GetNotificationsAsync();
        Assert.IsTrue(notifs.Ok);
        Assert.AreEqual(2, notifs.Unread);
        Assert.HasCount(2, notifs.Items);

        Assert.IsTrue(DreamPortApiClient.TryResolveSafeWebUrl("https://xmcraft.cn", notifs.Items[0].Link, out var safeUrl));
        Assert.AreEqual("https://xmcraft.cn/tasks", safeUrl);
        Assert.IsFalse(DreamPortApiClient.TryResolveSafeWebUrl("https://xmcraft.cn", notifs.Items[1].Link, out _));
        Assert.IsFalse(DreamPortApiClient.TryResolveSafeWebUrl("https://xmcraft.cn", "file:///etc/passwd", out _));
        Assert.IsFalse(DreamPortApiClient.TryResolveSafeWebUrl("https://xmcraft.cn", "//evil.example.com/x", out _));

        var u1 = await api.GetUnreadNotificationCountAsync();
        Assert.IsTrue(u1.Ok);
        Assert.AreEqual(2, u1.Unread);

        var m1 = await api.MarkNotificationReadAsync(11);
        Assert.IsTrue(m1.Ok);
        Assert.AreEqual(1, m1.Unread);

        var mall = await api.MarkAllNotificationsReadAsync();
        Assert.IsTrue(mall.Ok);
        Assert.AreEqual(0, mall.Unread);
        Assert.AreEqual(1, mall.Marked);
    }

    [TestMethod]
    public async Task AutoSigninService_DeduplicatesPerDay_AdvancesNextDay_AndStaysSilentOnFailure()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dp-autosignin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var currentDate = new DateOnly(2026, 11, 25);
            var mode = "network_fail";
            var callCount = 0;

            using var api = new DreamPortApiClient("https://xmcraft.cn", new StubHttpHandler((req, _) =>
            {
                if (req.RequestUri!.AbsolutePath != "/api/points/signin")
                    return JsonResponse(HttpStatusCode.NotFound, new { success = false });

                callCount++;
                return mode switch
                {
                    "network_fail" => throw new HttpRequestException("offline"),
                    "already" => JsonResponse(HttpStatusCode.BadRequest, new { success = false, message = "今日已签到" }),
                    _ => JsonResponse(HttpStatusCode.OK, new
                    {
                        success = true,
                        message = "签到成功",
                        data = new { first = true, alreadySigned = false, message = "签到成功", signedDate = currentDate.ToString("yyyy-MM-dd"), streak = 3, monthDays = 10, total = 20, pointsAwarded = 10 }
                    })
                };
            }), disposeHandler: true);
            api.SetToken("tok-auto");

            var service = new DreamPortAutoSigninService(tempDir, () => currentDate);

            // 未过审玩家跳过且不发请求
            var pendingOutcome = await service.TryAutoSigninAsync(api, "Steve", "pending");
            Assert.IsFalse(pendingOutcome.Attempted);
            Assert.AreEqual(0, callCount);

            // 网络失败：尝试过但不记录日期，也不弹错打扰
            var failOutcome = await service.TryAutoSigninAsync(api, "Steve", "approved");
            Assert.IsTrue(failOutcome.Attempted);
            Assert.IsFalse(failOutcome.ShouldNotifyUser);
            Assert.AreEqual(1, callCount);

            // 恢复网络首次成功：提示玩家并记录今日日期
            mode = "ok";
            var okOutcome = await service.TryAutoSigninAsync(api, "Steve", "approved");
            Assert.IsTrue(okOutcome.SignedNow);
            Assert.IsTrue(okOutcome.ShouldNotifyUser);
            StringAssert.Contains(okOutcome.NotificationText!, "+10 积分");
            Assert.AreEqual(2, callCount);

            // 同日再次启动：本地去重跳过，不发请求
            var secondSameDay = await service.TryAutoSigninAsync(api, "Steve", "approved");
            Assert.IsFalse(secondSameDay.Attempted);
            Assert.IsTrue(secondSameDay.AlreadySignedToday);
            Assert.IsFalse(secondSameDay.ShouldNotifyUser);
            Assert.AreEqual(2, callCount);

            // 次日且站点已签（比如在网页签过）：记入当日状态且静默
            currentDate = currentDate.AddDays(1);
            mode = "already";
            var webAlready = await service.TryAutoSigninAsync(api, "Steve", "approved");
            Assert.IsTrue(webAlready.Attempted);
            Assert.IsTrue(webAlready.AlreadySignedToday);
            Assert.IsFalse(webAlready.ShouldNotifyUser);
            Assert.AreEqual(3, callCount);
        }
        finally
        {
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
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public StubHttpHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> syncResponder)
            : this((req, ct) => Task.FromResult(syncResponder(req, ct)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request, cancellationToken);
    }
}
