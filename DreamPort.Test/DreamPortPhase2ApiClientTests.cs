using System.Net;
using System.Text;
using System.Text.Json;
using DreamPort.Core.Api;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamPort.Test;

[TestClass]
public sealed class DreamPortPhase2ApiClientTests
{
    [TestMethod]
    public async Task Sessions_ListAndRevoke_MapsFields_AndDistinguishesExpiredVsNetwork()
    {
        var calls = new List<(HttpMethod Method, string Path, string? Body)>();
        var handler = new StubHttpHandler(async (req, ct) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            var body = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            calls.Add((req.Method, path, body));

            if (path == "/api/auth/sessions")
            {
                return JsonResponse(HttpStatusCode.OK, new
                {
                    success = true,
                    data = new[]
                    {
                        new
                        {
                            sessionId = "sid-cur",
                            deviceName = "PCL-DreamPort (Mac)",
                            platform = "macos",
                            clientVersion = "0.2.0",
                            ip = "127.0.0.1",
                            createdAt = 1700000000000L,
                            lastSeenAt = 1700000005000L,
                            revoked = false,
                            current = true
                        },
                        new
                        {
                            sessionId = "sid-other",
                            deviceName = "Office PC",
                            platform = "windows",
                            clientVersion = "1.6.4",
                            ip = "10.0.0.8",
                            createdAt = 1700000000000L,
                            lastSeenAt = 1700000002000L,
                            revoked = false,
                            current = false
                        }
                    }
                });
            }

            if (path == "/api/auth/sessions/revoke")
                return JsonResponse(HttpStatusCode.OK, new { success = true, message = "已踢下线 1 个设备" });

            return JsonResponse(HttpStatusCode.NotFound, new { success = false });
        });

        using (var api = new DreamPortApiClient("https://xmcraft.cn", handler, disposeHandler: true))
        {
            api.SetToken("tok-1");
            var list = await api.GetSessionsAsync();
            Assert.IsTrue(list.Ok);
            Assert.HasCount(2, list.Sessions);
            Assert.IsTrue(list.Sessions[0].IsCurrent);
            Assert.IsFalse(list.Sessions[1].IsCurrent);

            var revokeSingle = await api.RevokeSessionAsync("sid-other");
            Assert.IsTrue(revokeSingle.Ok);
            Assert.IsTrue(calls.Last().Body!.Contains("sid-other", StringComparison.Ordinal));

            var revokeOthers = await api.RevokeSessionAsync(null);
            Assert.IsTrue(revokeOthers.Ok);
            Assert.AreEqual("{}", calls.Last().Body);
        }

        var unauthHandler = new StubHttpHandler((_, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.Unauthorized, new { success = false, message = "已过期" })));
        using (var api = new DreamPortApiClient("https://xmcraft.cn", unauthHandler, disposeHandler: true))
        {
            api.SetToken("expired");
            var res = await api.RevokeSessionAsync("sid-other");
            Assert.IsFalse(res.Ok);
            Assert.AreEqual(DreamPortFailureReason.Expired, res.FailureReason);
        }

        var netHandler = new StubHttpHandler((_, _) => throw new HttpRequestException("dns failed"));
        using (var api = new DreamPortApiClient("https://xmcraft.cn", netHandler, disposeHandler: true))
        {
            api.SetToken("tok-1");
            var res = await api.GetSessionsAsync();
            Assert.IsFalse(res.Ok);
            Assert.AreEqual(DreamPortFailureReason.Network, res.FailureReason);
        }
    }

    [TestMethod]
    public async Task EntryFlowEndpoints_ParseProgressQqMcVerifyMsaInviteAndAppeal()
    {
        var handler = new StubHttpHandler(async (req, ct) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);

            return path switch
            {
                "/api/user/progress" => JsonResponse(HttpStatusCode.OK, new
                {
                    success = true,
                    data = new
                    {
                        currentStep = "minecraft",
                        doneCount = 2,
                        totalCount = 6,
                        gated = true,
                        showBar = true,
                        needsAccountVerification = true,
                        qqBindMode = "required",
                        rejected = true,
                        banned = false,
                        bindHint = false,
                        steps = new object[]
                        {
                            new { key = "register", label = "注册", done = true, available = true, unavailableReason = "" },
                            new { key = "qq", label = "绑定 QQ", done = true, available = true, unavailableReason = "" },
                            new { key = "minecraft", label = "Minecraft ID 验证", done = false, available = true, unavailableReason = "" },
                            new { key = "questionnaire", label = "问卷入服 / 邀请入服", done = false, available = true, unavailableReason = "", via = (string?)null },
                            new { key = "review", label = "管理员审核", done = false, available = true, unavailableReason = "" },
                            new { key = "complete", label = "完成", done = false, available = true, unavailableReason = "" }
                        }
                    }
                }),
                "/api/user/qq/status" => JsonResponse(HttpStatusCode.OK, new
                {
                    success = true,
                    data = new { bound = false, qq = "", pendingQq = "123456" }
                }),
                "/api/user/qq/bind" => JsonResponse(HttpStatusCode.OK, new { success = true, message = "绑定成功" }),
                "/api/user/qq/unbind" => JsonResponse(HttpStatusCode.OK, new { success = true, message = "已解绑" }),
                "/api/user/minecraft/status" => JsonResponse(HttpStatusCode.OK, new
                {
                    success = true,
                    data = new { minecraftName = "Steve", minecraftUuid = "uuid-1", verified = false, rulesAccepted = true, status = "unverified", msaEnabled = true }
                }),
                "/api/user/minecraft/verify-token" => body.Contains("mc-jwt-123", StringComparison.Ordinal)
                    ? JsonResponse(HttpStatusCode.OK, new { success = true, message = "正版验证成功", data = new { verified = true } })
                    : JsonResponse(HttpStatusCode.BadRequest, new { success = false, message = "missing token" }),
                "/api/user/minecraft/msa/start" => JsonResponse(HttpStatusCode.OK, new
                {
                    success = true,
                    data = new { flowId = "f1", userCode = "ABCD-1234", verificationUri = "https://www.microsoft.com/link", expiresIn = 900, interval = 5 }
                }),
                "/api/user/minecraft/msa/poll" => JsonResponse(HttpStatusCode.OK, new
                {
                    success = true,
                    message = "验证完成",
                    data = new { status = "verified", interval = 5 }
                }),
                "/api/invite/apply" => JsonResponse(HttpStatusCode.OK, new { success = true, message = "已绑定邀请人" }),
                "/api/questionnaire/appeal/mine" => JsonResponse(HttpStatusCode.OK, new
                {
                    success = true,
                    data = new { found = true, status = "pending", adminReply = "", createdAt = 1700000000000L }
                }),
                "/api/questionnaire/appeal" => JsonResponse(HttpStatusCode.OK, new { success = true, message = "申诉已提交，等待管理员处理" }),
                _ => JsonResponse(HttpStatusCode.NotFound, new { success = false })
            };
        });

        using var api = new DreamPortApiClient("https://xmcraft.cn", handler, disposeHandler: true);
        api.SetToken("tok-1");

        var progressRes = await api.GetProgressAsync();
        Assert.IsTrue(progressRes.Ok);
        Assert.IsNotNull(progressRes.Progress);
        Assert.AreEqual("minecraft", progressRes.Progress.CurrentStep);
        Assert.IsTrue(progressRes.Progress.ShowBar);
        Assert.IsTrue(progressRes.Progress.Rejected);
        Assert.HasCount(6, progressRes.Progress.Steps);

        var qqStatus = await api.GetQqStatusAsync();
        Assert.IsNotNull(qqStatus.Status);
        Assert.AreEqual("123456", qqStatus.Status.PendingQq);
        Assert.IsTrue((await api.BindQqAsync("888999")).Ok);
        Assert.IsTrue((await api.UnbindQqAsync()).Ok);

        var mcStatus = await api.GetMinecraftStatusAsync();
        Assert.IsNotNull(mcStatus.Status);
        Assert.IsTrue(mcStatus.Status.MsaEnabled);
        Assert.IsTrue((await api.VerifyMinecraftByTokenAsync("mc-jwt-123")).Ok);

        var msaStart = await api.StartMsaVerifyAsync();
        Assert.IsTrue(msaStart.Ok);
        Assert.AreEqual("ABCD-1234", msaStart.UserCode);
        var msaPoll = await api.PollMsaVerifyAsync("f1");
        Assert.AreEqual("verified", msaPoll.Status);

        Assert.IsTrue((await api.ApplyInviteCodeAsync("INV-2026")).Ok);

        var appealMine = await api.GetMyAppealAsync();
        Assert.IsNotNull(appealMine.Appeal);
        Assert.IsTrue(appealMine.Appeal.Found);
        Assert.AreEqual("pending", appealMine.Appeal.Status);
        Assert.IsTrue((await api.SubmitAppealAsync("第一题的规则描述我理解有误")).Ok);
    }

    [TestMethod]
    public async Task Questionnaire_StreamParsesSseEvents_AndFallsBackToSubmitOnTruncatedStream()
    {
        var sseEvents = new List<DreamPortQuestionScoreEvent>();
        var summarizingCount = 0;

        var sseHandler = new StubHttpHandler((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/questionnaire/config")
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, new
                {
                    success = true,
                    data = new
                    {
                        enabled = true,
                        passScore = 60,
                        questions = new object[]
                        {
                            new
                            {
                                id = 11L,
                                text = "是否同意服务器规则？",
                                type = "single",
                                required = true,
                                max_score = 50,
                                multiline = false,
                                maxLength = 0,
                                placeholder = "",
                                input = new { min_length = 0, max_length = 0, min_selections = 0, max_selections = 0 },
                                options = new[] { new { id = 101L, text = "同意" }, new { id = 102L, text = "不同意" } }
                            },
                            new
                            {
                                id = 12L,
                                text = "自我介绍",
                                type = "text",
                                required = true,
                                max_score = 50,
                                multiline = true,
                                maxLength = 500,
                                placeholder = "写下常玩方向",
                                input = new { min_length = 5, max_length = 500, min_selections = 0, max_selections = 0 },
                                options = Array.Empty<object>()
                            }
                        }
                    }
                }));
            }

            if (req.RequestUri!.AbsolutePath == "/api/questionnaire/stream")
            {
                var sse = string.Join("\n",
                    "data: {\"type\":\"question_scored\",\"index\":0,\"total\":2,\"questionId\":11,\"questionText\":\"是否同意服务器规则？\",\"score\":50,\"maxScore\":50,\"reason\":\"回答正确\",\"totalScore\":50,\"maxScoreTotal\":50}",
                    "",
                    "data: {\"type\":\"summarizing\"}",
                    "",
                    "data: {\"type\":\"complete\",\"totalScore\":95,\"maxScore\":100,\"passed\":true,\"summary\":\"态度诚恳，欢迎加入\"}",
                    "",
                    "");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
                });
            }

            return Task.FromResult(JsonResponse(HttpStatusCode.NotFound, new { success = false }));
        });

        using (var api = new DreamPortApiClient("https://xmcraft.cn", sseHandler, disposeHandler: true))
        {
            api.SetToken("tok-1");
            var (cfg, reason, err) = await api.GetQuestionnaireConfigAsync();
            Assert.IsNull(reason);
            Assert.IsNull(err);
            Assert.IsNotNull(cfg);
            Assert.IsTrue(cfg.Enabled);
            Assert.AreEqual(60, cfg.PassScore);
            Assert.HasCount(2, cfg.Questions);

            var outcome = await api.SubmitQuestionnaireStreamAsync(
                new Dictionary<string, object> { ["11"] = 101L, ["12"] = "喜欢红石和建筑" },
                onScored: ev => sseEvents.Add(ev),
                onSummarizing: () => summarizingCount++);

            Assert.IsTrue(outcome.Ok);
            Assert.IsTrue(outcome.Passed);
            Assert.AreEqual(95, outcome.TotalScore);
            Assert.AreEqual(100, outcome.MaxScore);
            Assert.AreEqual("态度诚恳，欢迎加入", outcome.Summary);
            Assert.HasCount(1, sseEvents);
            Assert.AreEqual(1, summarizingCount);
        }

        // 流式端点在未发 complete 时提前断开：必须自动回退 /api/questionnaire/submit
        var fallbackHandler = new StubHttpHandler((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/questionnaire/stream")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("data: {\"type\":\"question_scored\",\"index\":0,\"total\":1,\"questionId\":11,\"questionText\":\"Q1\",\"score\":40,\"maxScore\":50,\"reason\":\"ok\",\"totalScore\":40,\"maxScoreTotal\":50}\n\n",
                        Encoding.UTF8,
                        "text/event-stream")
                });
            }

            if (req.RequestUri!.AbsolutePath == "/api/questionnaire/submit")
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, new
                {
                    totalScore = 88,
                    maxScore = 100,
                    passed = true,
                    summary = "同步提交回退成功"
                }));
            }

            return Task.FromResult(JsonResponse(HttpStatusCode.NotFound, new { success = false }));
        });

        using (var api = new DreamPortApiClient("https://xmcraft.cn", fallbackHandler, disposeHandler: true))
        {
            api.SetToken("tok-1");
            var outcome = await api.SubmitQuestionnaireStreamAsync(new Dictionary<string, object> { ["11"] = 101L });
            Assert.IsTrue(outcome.Ok);
            Assert.AreEqual(88, outcome.TotalScore);
            Assert.AreEqual("同步提交回退成功", outcome.Summary);
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
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request, cancellationToken);
    }
}
