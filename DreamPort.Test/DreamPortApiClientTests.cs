using System.Net;
using System.Text;
using System.Text.Json;
using DreamPort.Core.Api;
using DreamPort.Core.Auth;
using DreamPort.Core.Config;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamPort.Test;

[TestClass]
public sealed class DreamPortApiClientTests
{
    [TestMethod]
    public async Task CreateBrowserLogin_SendsDeviceHeaders_AndAbsolutizesConfirmUrl()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHttpHandler((req, _) =>
        {
            captured = req;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, new
            {
                success = true,
                data = new
                {
                    qrToken = "qr-tok-123",
                    approveCode = "app-456",
                    qrUrl = "/qr-login?code=app-456",
                    expiresAt = 1900000000000L
                }
            }));
        });

        using var api = new DreamPortApiClient("https://xmcraft.cn/", handler, disposeHandler: true);
        var (session, error) = await api.CreateBrowserLoginAsync();

        Assert.IsNull(error);
        Assert.IsNotNull(session);
        Assert.AreEqual("qr-tok-123", session.QrToken);
        Assert.AreEqual("app-456", session.ApproveCode);
        Assert.AreEqual("https://xmcraft.cn/qr-login?code=app-456", session.ConfirmUrl);
        Assert.AreEqual(1900000000000L, session.ExpiresAtMs);

        Assert.IsNotNull(captured);
        Assert.AreEqual(HttpMethod.Post, captured.Method);
        Assert.AreEqual("https://xmcraft.cn/api/qr-login/create", captured.RequestUri!.ToString());
        Assert.AreEqual("desktop", captured.Headers.GetValues("X-DreamPort-Surface").Single());
        Assert.StartsWith("PCL-DreamPort (", captured.Headers.GetValues("X-Device-Name").Single());
        Assert.AreEqual(DreamPortBuildConfig.UserAgent, captured.Headers.UserAgent.ToString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(captured.Headers.GetValues("X-Device-Platform").Single()));
        Assert.IsFalse(string.IsNullOrWhiteSpace(captured.Headers.GetValues("X-Client-Version").Single()));
    }

    [TestMethod]
    public async Task PollBrowserLogin_MapsAllStates_NotFound_AndNetworkError()
    {
        var cases = new (HttpStatusCode Code, string StatusText, DreamPortBrowserLoginStatus Expected, string? User, bool Admin)[]
        {
            (HttpStatusCode.OK, "pending", DreamPortBrowserLoginStatus.Pending, null, false),
            (HttpStatusCode.OK, "approved", DreamPortBrowserLoginStatus.Approved, "Steve", true),
            (HttpStatusCode.OK, "denied", DreamPortBrowserLoginStatus.Denied, null, false),
            (HttpStatusCode.OK, "expired", DreamPortBrowserLoginStatus.Expired, null, false),
            (HttpStatusCode.OK, "consumed", DreamPortBrowserLoginStatus.Consumed, null, false)
        };

        foreach (var item in cases)
        {
            var h = new StubHttpHandler((_, _) => Task.FromResult(JsonResponse(item.Code, new
            {
                success = true,
                data = new { status = item.StatusText, username = item.User, isAdmin = item.Admin }
            })));
            using var api = new DreamPortApiClient("https://xmcraft.cn", h, disposeHandler: true);
            var poll = await api.PollBrowserLoginAsync("qr-tok");
            Assert.AreEqual(item.Expected, poll.Status);
            Assert.AreEqual(item.User, poll.Username);
            Assert.AreEqual(item.Admin, poll.IsAdmin);
        }

        var notFoundHandler = new StubHttpHandler((_, _) => Task.FromResult(JsonResponse(
            HttpStatusCode.NotFound,
            new { success = false, message = "登录会话不存在" })));
        using (var api = new DreamPortApiClient("https://xmcraft.cn", notFoundHandler, disposeHandler: true))
        {
            var poll = await api.PollBrowserLoginAsync("missing");
            Assert.AreEqual(DreamPortBrowserLoginStatus.NotFound, poll.Status);
        }

        var netHandler = new StubHttpHandler((_, _) => throw new HttpRequestException("connection reset"));
        using (var api = new DreamPortApiClient("https://xmcraft.cn", netHandler, disposeHandler: true))
        {
            var poll = await api.PollBrowserLoginAsync("qr-tok");
            Assert.AreEqual(DreamPortBrowserLoginStatus.NetworkError, poll.Status);
        }
    }

    [TestMethod]
    public async Task WaitBrowserLoginChange_WakesOnSseData_AndHandlesFailureGracefully()
    {
        var sseHandler = new StubHttpHandler((req, _) =>
        {
            Assert.AreEqual("https://xmcraft.cn/api/qr-login/wait?token=t1", req.RequestUri!.ToString());
            var ssePayload = "event: message\ndata: {\"type\":\"changed\"}\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ssePayload, Encoding.UTF8, "text/event-stream")
            });
        });

        using (var api = new DreamPortApiClient("https://xmcraft.cn", sseHandler, disposeHandler: true))
        {
            var changed = await api.WaitBrowserLoginChangeAsync("t1", TimeSpan.FromSeconds(5));
            Assert.IsTrue(changed);
        }

        var failHandler = new StubHttpHandler((_, _) => throw new HttpRequestException("offline"));
        using (var api = new DreamPortApiClient("https://xmcraft.cn", failHandler, disposeHandler: true))
        {
            var changed = await api.WaitBrowserLoginChangeAsync("t1", TimeSpan.FromSeconds(5));
            Assert.IsFalse(changed);
        }
    }

    [TestMethod]
    public async Task ConsumeBrowserLogin_SetsTokenWithDeviceHeaders_AndHandlesNonApproved()
    {
        HttpRequestMessage? captured = null;
        var okHandler = new StubHttpHandler((req, _) =>
        {
            captured = req;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, new
            {
                success = true,
                data = new
                {
                    token = "client-session-jwt",
                    username = "Steve",
                    isAdmin = false
                }
            }));
        });

        using (var api = new DreamPortApiClient("https://xmcraft.cn", okHandler, disposeHandler: true))
        {
            var res = await api.ConsumeBrowserLoginAsync("qr-ok");
            Assert.IsTrue(res.Ok);
            Assert.AreEqual(DreamPortBrowserLoginStatus.Approved, res.Status);
            Assert.AreEqual("client-session-jwt", res.Token);
            Assert.AreEqual("client-session-jwt", api.CurrentToken);
            Assert.AreEqual("Steve", res.Username);
            Assert.IsFalse(res.IsAdmin);
            Assert.IsNotNull(captured);
            Assert.AreEqual(HttpMethod.Post, captured.Method);
            Assert.AreEqual("https://xmcraft.cn/api/qr-login/consume?token=qr-ok", captured.RequestUri!.ToString());
            Assert.StartsWith("PCL-DreamPort (", captured.Headers.GetValues("X-Device-Name").Single());
        }

        var consumedHandler = new StubHttpHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, new
        {
            success = false,
            message = "该登录凭据已被使用",
            data = new { status = "consumed" }
        })));
        using (var api = new DreamPortApiClient("https://xmcraft.cn", consumedHandler, disposeHandler: true))
        {
            var res = await api.ConsumeBrowserLoginAsync("qr-used");
            Assert.IsFalse(res.Ok);
            Assert.AreEqual(DreamPortBrowserLoginStatus.Consumed, res.Status);
            Assert.IsNull(api.CurrentToken);
        }
    }

    [TestMethod]
    public async Task GetConfigAsync_ParsesSiteMetadata()
    {
        var handler = new StubHttpHandler((req, _) =>
        {
            Assert.AreEqual("/api/config", req.RequestUri!.AbsolutePath);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, new
            {
                success = true,
                data = new
                {
                    siteName = "夏日小镇",
                    publicUrl = "https://xmcraft.cn",
                    captcha = new { login = true }
                }
            }));
        });

        using var api = new DreamPortApiClient("https://xmcraft.cn", handler, disposeHandler: true);
        var cfg = await api.GetConfigAsync();
        Assert.IsNotNull(cfg);
        Assert.AreEqual("夏日小镇", cfg.SiteName);
        Assert.AreEqual("https://xmcraft.cn", cfg.PublicUrl);
        Assert.IsTrue(cfg.LoginCaptchaRequired);
    }

    [TestMethod]
    public async Task ValidateSession_DistinguishesNetworkVsExpired_AndRenewsOn401()
    {
        var networkDown = new StubHttpHandler((_, _) => throw new HttpRequestException("DNS timeout"));
        using (var api = new DreamPortApiClient("https://xmcraft.cn", networkDown, disposeHandler: true))
        {
            api.SetToken("keep-my-token");
            var vNet = await api.ValidateSessionAsync();
            Assert.IsFalse(vNet.Valid);
            Assert.AreEqual(DreamPortFailureReason.Network, vNet.Reason);
            Assert.AreEqual("keep-my-token", api.CurrentToken);
        }

        var validateCount = 0;
        var renewHandler = new StubHttpHandler((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/auth/validate")
            {
                validateCount++;
                if (validateCount == 1)
                {
                    return Task.FromResult(JsonResponse(HttpStatusCode.Unauthorized, new { message = "expired" }));
                }

                var auth = req.Headers.Authorization?.Parameter;
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, new
                {
                    success = true,
                    data = new { valid = auth == "renewed-token", username = "Alice", isAdmin = false }
                }));
            }

            if (req.RequestUri!.AbsolutePath == "/api/auth/renew" && req.Method == HttpMethod.Post)
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, new
                {
                    success = true,
                    data = new { token = "renewed-token" }
                }));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        using (var api = new DreamPortApiClient("https://xmcraft.cn", renewHandler, disposeHandler: true))
        {
            api.SetToken("stale-token");
            var ok = await api.ValidateSessionAsync();
            Assert.IsTrue(ok.Valid);
            Assert.AreEqual("Alice", ok.Username);
            Assert.AreEqual("renewed-token", api.CurrentToken);
        }

        var rejectedHandler = new StubHttpHandler((_, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.Unauthorized, new { message = "revoked" })));
        using (var api = new DreamPortApiClient("https://xmcraft.cn", rejectedHandler, disposeHandler: true))
        {
            api.SetToken("revoked-token");
            var expired = await api.ValidateSessionAsync();
            Assert.IsFalse(expired.Valid);
            Assert.AreEqual(DreamPortFailureReason.Expired, expired.Reason);
        }
    }

    [TestMethod]
    public void SessionStore_NeverWritesPlaintextTokenToDisk()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dp-store-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var persistentVault = new FakeVault(enabled: true);
            var store1 = new DreamPortSessionStore(tempDir, persistentVault);
            var saved = store1.Save("https://xmcraft.cn/", "Steve", false, "super-secret-jwt-999");

            Assert.IsTrue(saved.IsTokenPersistent);
            var sessionJson = File.ReadAllText(Path.Combine(tempDir, "dreamport-session.json"));
            Assert.IsFalse(sessionJson.Contains("super-secret-jwt-999", StringComparison.Ordinal));

            var reloaded = new DreamPortSessionStore(tempDir, persistentVault).Load();
            Assert.IsTrue(reloaded.IsTokenPersistent);
            Assert.AreEqual("super-secret-jwt-999", reloaded.Token);

            var memoryOnlyVault = new FakeVault(enabled: false);
            var store2 = new DreamPortSessionStore(tempDir, memoryOnlyVault);
            var memSaved = store2.Save("https://xmcraft.cn", "Alex", true, "ephemeral-secret-777");
            Assert.IsFalse(memSaved.IsTokenPersistent);
            Assert.AreEqual("ephemeral-secret-777", memSaved.Token);
            Assert.AreEqual("ephemeral-secret-777", store2.Load().Token);

            var sessionJson2 = File.ReadAllText(Path.Combine(tempDir, "dreamport-session.json"));
            Assert.IsFalse(sessionJson2.Contains("ephemeral-secret-777", StringComparison.Ordinal));

            var coldReload = new DreamPortSessionStore(tempDir, memoryOnlyVault).Load();
            Assert.IsNull(coldReload.Token);
            Assert.AreEqual("Alex", coldReload.Username);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void BuildConfig_KeepsUpdateMirrorAndCleanDownloadUserAgent()
    {
        Assert.AreEqual("https://dl.xmcraft.cn", DreamPortBuildConfig.UpdateMirrorBaseUrl);

        var dlUa = DreamPortBuildConfig.DownloadUserAgent;
        Assert.StartsWith("PCL-DreamPort/", dlUa);
        Assert.IsFalse(dlUa.Contains('('));
        Assert.IsFalse(dlUa.Contains(')'));
        Assert.IsFalse(dlUa.Contains("://", StringComparison.Ordinal));
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

    private sealed class FakeVault(bool enabled) : IDreamPortSecretVault
    {
        public bool TrySaveSecret(string accountKey, string secret, out string? encryptedPayload)
        {
            if (!enabled)
            {
                encryptedPayload = null;
                return false;
            }

            encryptedPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes("wrapped:" + secret));
            return true;
        }

        public bool TryLoadSecret(string accountKey, string? encryptedPayload, out string? secret)
        {
            secret = null;
            if (!enabled || string.IsNullOrEmpty(encryptedPayload))
                return false;

            var text = Encoding.UTF8.GetString(Convert.FromBase64String(encryptedPayload));
            if (!text.StartsWith("wrapped:", StringComparison.Ordinal))
                return false;

            secret = text["wrapped:".Length..];
            return true;
        }

        public void DeleteSecret(string accountKey)
        {
        }
    }
}
