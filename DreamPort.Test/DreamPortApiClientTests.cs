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
    public async Task Login_AttachesDesktopHeaders_AndHandlesOk()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new StubHttpHandler(async (req, _) =>
        {
            captured = req;
            capturedBody = await req.Content!.ReadAsStringAsync();
            return JsonResponse(HttpStatusCode.OK, new
            {
                success = true,
                data = new
                {
                    token = "jwt-ok-123",
                    username = "Steve",
                    status = "active",
                    isAdmin = true
                }
            });
        });

        using var api = new DreamPortApiClient("https://xmcraft.cn/", handler, disposeHandler: true);
        var res = await api.LoginAsync("Steve", "pass123");

        Assert.AreEqual(DreamPortLoginOutcome.Ok, res.Outcome);
        Assert.AreEqual("jwt-ok-123", res.Token);
        Assert.AreEqual("jwt-ok-123", api.CurrentToken);
        Assert.AreEqual("Steve", res.Username);
        Assert.IsTrue(res.IsAdmin);
        Assert.IsNotNull(captured);
        Assert.AreEqual("https://xmcraft.cn/api/login", captured.RequestUri!.ToString());
        Assert.AreEqual("desktop", captured.Headers.GetValues("X-DreamPort-Surface").Single());
        Assert.AreEqual(DreamPortBuildConfig.UserAgent, captured.Headers.UserAgent.ToString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(captured.Headers.GetValues("X-Device-Platform").Single()));
        Assert.IsFalse(string.IsNullOrWhiteSpace(captured.Headers.GetValues("X-Client-Version").Single()));
        Assert.IsTrue(capturedBody!.Contains("\"username\":\"Steve\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Login_Need2Fa_NeedCaptcha_AndBannedWithRelativeEvidence()
    {
        var handler2Fa = new StubHttpHandler((_, _) => Task.FromResult(JsonResponse(
            HttpStatusCode.OK,
            new
            {
                success = true,
                data = new
                {
                    status = "needs_2fa",
                    username = "AdminUser",
                    isAdmin = true,
                    challengeId = "chg-999",
                    passkey = true
                }
            })));

        using (var api = new DreamPortApiClient("https://xmcraft.cn", handler2Fa, disposeHandler: true))
        {
            var r2 = await api.LoginAsync("AdminUser", "pass");
            Assert.AreEqual(DreamPortLoginOutcome.Need2Fa, r2.Outcome);
            Assert.AreEqual("chg-999", r2.ChallengeId);
            Assert.IsTrue(r2.SupportsPasskey);
            Assert.IsNull(api.CurrentToken);
        }

        var handlerCaptcha = new StubHttpHandler((_, _) => Task.FromResult(JsonResponse(
            HttpStatusCode.BadRequest,
            new { success = false, message = "请完成图形验证码校验" })));

        using (var api = new DreamPortApiClient("https://xmcraft.cn", handlerCaptcha, disposeHandler: true))
        {
            var rc = await api.LoginAsync("Steve", "pass");
            Assert.AreEqual(DreamPortLoginOutcome.NeedCaptcha, rc.Outcome);
        }

        var handlerBanned = new StubHttpHandler((_, _) => Task.FromResult(JsonResponse(
            HttpStatusCode.Forbidden,
            new
            {
                success = false,
                data = new
                {
                    banned = true,
                    username = "BadPlayer",
                    reason = "违规破坏",
                    banUntil = 1900000000000L,
                    evidence = new[]
                    {
                        new { id = "e1", fileUrl = "/uploads/ban-proof.png", note = "截图", mediaType = "image/png" }
                    }
                }
            })));

        using (var api = new DreamPortApiClient("https://xmcraft.cn", handlerBanned, disposeHandler: true))
        {
            var rb = await api.LoginAsync("BadPlayer", "pass");
            Assert.AreEqual(DreamPortLoginOutcome.Banned, rb.Outcome);
            Assert.AreEqual("违规破坏", rb.BanReason);
            Assert.HasCount(1, rb.Evidence);
            Assert.AreEqual("https://xmcraft.cn/uploads/ban-proof.png", rb.Evidence[0].FileUrl);
        }
    }

    [TestMethod]
    public async Task CaptchaAndConfig_SupportBareAndWrappedJson()
    {
        var handler = new StubHttpHandler((req, _) =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/api/config")
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, new
                {
                    success = true,
                    data = new
                    {
                        siteName = "夏日小镇",
                        captcha = new { login = true }
                    }
                }));
            }

            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/api/captcha/generate")
            {
                // 服务端裸对象返回，未包 {success, data}
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, new
                {
                    captchaToken = "cap-bare-1",
                    image = "data:image/png;base64,iVBORw0KGgo="
                }));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        using var api = new DreamPortApiClient("https://xmcraft.cn", handler, disposeHandler: true);
        Assert.IsTrue(await api.IsLoginCaptchaRequiredAsync());

        var cap = await api.GenerateCaptchaAsync();
        Assert.IsNotNull(cap);
        Assert.AreEqual("cap-bare-1", cap.CaptchaToken);
        Assert.StartsWith("data:image/png;base64,", cap.ImageDataUri);
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
