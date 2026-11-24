using System.Net;
using System.Text;
using System.Text.Json;
using DreamPort.Core.Api;
using DreamPort.Core.Auth;
using DreamPort.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamPort.Test;

[TestClass]
public sealed class DreamPortAccountPhase2ViewModelTests
{
    [TestMethod]
    public async Task SwitchSite_LogsOutOldSite_AndClearsTokenBeforeSwitching()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dp-vm-switch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var calls = new List<string>();
            var vault = new InMemoryVault();
            var store = new DreamPortSessionStore(tempDir, vault);
            store.Save("https://site-a.example.com", "Steve", false, "token-site-a");

            using var vm = new DreamPortAccountViewModel(
                store: store,
                clientFactory: url => new DreamPortApiClient(url, new StubHttpHandler((req, _) =>
                {
                    calls.Add(req.RequestUri!.ToString());
                    if (req.RequestUri.AbsolutePath == "/api/logout")
                        return Task.FromResult(JsonResponse(HttpStatusCode.OK, new { success = true }));
                    if (req.RequestUri.AbsolutePath == "/api/config")
                        return Task.FromResult(JsonResponse(HttpStatusCode.OK, new { success = true, data = new { siteName = "Site B" } }));
                    return Task.FromResult(JsonResponse(HttpStatusCode.NotFound, new { success = false }));
                }), disposeHandler: true),
                openUrl: _ => { });

            Assert.IsTrue(vm.IsLoggedIn);
            vm.BaseUrl = "https://site-b.example.com/";
            await vm.CheckServerCommand.ExecuteAsync(null);

            Assert.IsTrue(calls.Contains("https://site-a.example.com/api/logout"), "切换站点前必须先调用旧站点的 /api/logout");
            Assert.IsTrue(calls.Contains("https://site-b.example.com/api/config"));
            Assert.IsFalse(vm.IsLoggedIn);
            Assert.AreEqual("https://site-b.example.com", vm.BaseUrl);
            Assert.IsNull(store.Load().Token);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task RefreshEntryFlow_OneClickMcVerify_AppealValidation_AndSessionRevoke()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dp-vm-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var mcVerified = false;
            var qqBound = false;
            var revokedOther = false;
            var store = new DreamPortSessionStore(tempDir, new InMemoryVault());
            store.Save("https://xmcraft.cn", "Alex", false, "tok-alex");

            using var vm = new DreamPortAccountViewModel(
                store: store,
                clientFactory: url => new DreamPortApiClient(url, new StubHttpHandler(async (req, ct) =>
                {
                    var path = req.RequestUri!.AbsolutePath;
                    var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);

                    switch (path)
                    {
                        case "/api/auth/validate":
                            return JsonResponse(HttpStatusCode.OK, new { success = true, data = new { valid = true, username = "Alex", isAdmin = false } });
                        case "/api/user/profile":
                            return JsonResponse(HttpStatusCode.OK, new
                            {
                                success = true,
                                data = new { username = "Alex", status = mcVerified ? "approved" : "rejected", minecraftName = "AlexMC", points = 120 }
                            });
                        case "/api/user/progress":
                            return JsonResponse(HttpStatusCode.OK, new
                            {
                                success = true,
                                data = new
                                {
                                    currentStep = mcVerified ? "complete" : "minecraft",
                                    doneCount = mcVerified ? 6 : 2,
                                    totalCount = 6,
                                    gated = !mcVerified,
                                    showBar = !mcVerified,
                                    needsAccountVerification = !mcVerified,
                                    qqBindMode = "required",
                                    rejected = !mcVerified,
                                    banned = false,
                                    bindHint = false,
                                    steps = new object[]
                                    {
                                        new { key = "register", label = "注册", done = true, available = true, unavailableReason = "" },
                                        new { key = "qq", label = "绑定 QQ", done = qqBound, available = true, unavailableReason = "" },
                                        new { key = "minecraft", label = "Minecraft ID 验证", done = mcVerified, available = true, unavailableReason = "" }
                                    }
                                }
                            });
                        case "/api/user/qq/status":
                            return JsonResponse(HttpStatusCode.OK, new { success = true, data = new { bound = qqBound, qq = qqBound ? "10***89" : "", pendingQq = "" } });
                        case "/api/user/qq/bind":
                            qqBound = true;
                            return JsonResponse(HttpStatusCode.OK, new { success = true, message = "QQ 绑定成功" });
                        case "/api/user/minecraft/status":
                            return JsonResponse(HttpStatusCode.OK, new { success = true, data = new { minecraftName = "AlexMC", verified = mcVerified, msaEnabled = true } });
                        case "/api/user/minecraft/verify-token":
                            Assert.IsTrue(body.Contains("ms-token-ok", StringComparison.Ordinal));
                            mcVerified = true;
                            return JsonResponse(HttpStatusCode.OK, new { success = true, message = "Minecraft 正版验证成功" });
                        case "/api/questionnaire/appeal/mine":
                            return JsonResponse(HttpStatusCode.OK, new { success = true, data = new { found = false } });
                        case "/api/questionnaire/appeal":
                            return JsonResponse(HttpStatusCode.OK, new { success = true, message = "申诉已提交" });
                        case "/api/auth/sessions":
                            return JsonResponse(HttpStatusCode.OK, new
                            {
                                success = true,
                                data = revokedOther
                                    ? new object[]
                                    {
                                        new { sessionId = "cur", deviceName = "Mac", platform = "macos", clientVersion = "0.2", ip = "1.1.1.1", lastSeenAt = 1700000001000L, revoked = false, current = true }
                                    }
                                    : new object[]
                                    {
                                        new { sessionId = "cur", deviceName = "Mac", platform = "macos", clientVersion = "0.2", ip = "1.1.1.1", lastSeenAt = 1700000001000L, revoked = false, current = true },
                                        new { sessionId = "old", deviceName = "Win", platform = "windows", clientVersion = "1.6", ip = "2.2.2.2", lastSeenAt = 1700000000000L, revoked = false, current = false }
                                    }
                            });
                        case "/api/auth/sessions/revoke":
                            revokedOther = true;
                            return JsonResponse(HttpStatusCode.OK, new { success = true, message = "已踢下线 1 个设备" });
                        default:
                            return JsonResponse(HttpStatusCode.NotFound, new { success = false });
                    }
                }), disposeHandler: true),
                openUrl: _ => { },
                acquireMcToken: _ => Task.FromResult<(string?, string?, string?)>(("ms-token-ok", "AlexMC", null)),
                getActiveMcProfileName: () => "AlexMC");

            await vm.InitializeAsync();
            Assert.IsTrue(vm.IsLoggedIn);
            Assert.IsTrue(vm.ShowEntryFlowCard);
            Assert.IsTrue(vm.IsRejected);
            Assert.IsFalse(vm.CanManageSessions);
            Assert.AreEqual("AlexMC", vm.ActiveMcProfileName);

            // 申诉不足 10 字在本地被拦截
            vm.AppealReasonInput = "太短了";
            await vm.SubmitAppealCommand.ExecuteAsync(null);
            Assert.IsTrue(vm.IsStatusError);

            // 绑定 QQ + 一键验证正版 ID -> 状态推进为 approved 且可踢其它设备
            vm.QqBindCodeInput = "123456";
            await vm.BindQqCommand.ExecuteAsync(null);
            Assert.IsTrue(qqBound);

            await vm.VerifyMinecraftWithCurrentProfileCommand.ExecuteAsync(null);
            Assert.IsTrue(mcVerified);
            Assert.IsTrue(vm.IsApprovedPlayer);
            Assert.IsFalse(vm.ShowEntryFlowCard);
            Assert.IsTrue(vm.CanManageSessions);
            Assert.HasCount(2, vm.SessionItems);

            await vm.RevokeOtherSessionsCommand.ExecuteAsync(null);
            Assert.HasCount(1, vm.SessionItems);
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
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request, cancellationToken);
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
