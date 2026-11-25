using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DreamPort.Core.Api;
using DreamPort.Core.Services;
using DreamPort.Core.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamPort.Test;

[TestClass]
public sealed class DreamPortHeartbeatAndUpdateTests
{
    [TestMethod]
    public async Task SendMinimalHeartbeatAsync_SendsOnlyMinimalMetadata_AndIgnoresAssistFields()
    {
        string? capturedBody = null;
        string? capturedPath = null;

        using var api = new DreamPortApiClient("https://xmcraft.cn", new StubHttpHandler((req, _) =>
        {
            capturedPath = req.RequestUri!.AbsolutePath;
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(HttpStatusCode.OK, new
            {
                success = true,
                data = new
                {
                    nextIntervalSec = 45,
                    defaultsVersion = 99,
                    actions = new[] { new { type = "applyDefaults", payload = "evil" } },
                    assist = new { enabled = true }
                }
            });
        }));
        api.SetToken("test-token");

        var startedAt = 1_764_000_000_000L;
        var res = await api.SendMinimalHeartbeatAsync("0.2.0", "macos", startedAt);

        Assert.IsTrue(res.Ok);
        Assert.AreEqual(45, res.NextIntervalSec);
        Assert.AreEqual("/api/client/heartbeat", capturedPath);
        Assert.IsNotNull(capturedBody);

        // 绝不允许上传任何模组/设置/路径/代理/硬件诊断快照
        foreach (var forbidden in new[]
                 {
                     "mods", "modNames", "settings", "javaPath", "proxy",
                     "lastLaunch", "capabilities", "defaultsVersion", "actions", "assist"
                 })
        {
            Assert.IsFalse(
                capturedBody.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"心跳请求体不得包含字段 {forbidden}");
        }

        using var doc = JsonDocument.Parse(capturedBody);
        Assert.AreEqual(startedAt, doc.RootElement.GetProperty("clientStartedAt").GetInt64());
        var snapshotJson = doc.RootElement.GetProperty("snapshot").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(snapshotJson));

        using var snapDoc = JsonDocument.Parse(snapshotJson!);
        var props = snapDoc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToArray();
        CollectionAssert.AreEqual(new[] { "clientStartedAt", "clientVersion", "platform" }, props);
        Assert.AreEqual("0.2.0", snapDoc.RootElement.GetProperty("clientVersion").GetString());
        Assert.AreEqual("macos", snapDoc.RootElement.GetProperty("platform").GetString());
        Assert.AreEqual(startedAt, snapDoc.RootElement.GetProperty("clientStartedAt").GetInt64());
    }

    [TestMethod]
    public async Task HeartbeatService_HandlesDynamicIntervalNetworkBackoffUnauthorizedAndOffline()
    {
        var beatCount = 0;
        var offlineCalled = 0;
        var delays = new List<TimeSpan>();
        var stoppedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var api = new DreamPortApiClient("https://xmcraft.cn", new StubHttpHandler((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/client/offline")
            {
                Interlocked.Increment(ref offlineCalled);
                return JsonResponse(HttpStatusCode.OK, new { ok = true });
            }

            if (path == "/api/client/heartbeat")
            {
                var step = Interlocked.Increment(ref beatCount);
                return step switch
                {
                    1 => JsonResponse(HttpStatusCode.OK, new { success = true, data = new { nextIntervalSec = 30 } }),
                    2 => throw new HttpRequestException("temporary network failure"),
                    3 => JsonResponse(HttpStatusCode.ServiceUnavailable, new { success = false, message = "busy" }),
                    _ => JsonResponse(HttpStatusCode.Unauthorized, new { success = false, message = "expired" })
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        api.SetToken("live-token");

        await using var service = new DreamPortHeartbeatService(
            getClient: () => api,
            isLoggedIn: () => true,
            ownsClientInstances: false,
            baseBackoffMs: 25,
            getClientVersion: () => "0.2.0",
            getPlatform: () => "macos",
            delayAsync: (delay, ct) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        service.Start("unit-test");

        // 等待 4 轮心跳直到第 4 次返回 401 自动停跳
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (service.IsRunning && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
        }

        if (!service.IsRunning)
            stoppedTcs.TrySetResult();

        Assert.IsTrue(stoppedTcs.Task.IsCompleted, "401 响应应自动停止心跳循环");
        Assert.AreEqual(4, beatCount);
        Assert.AreEqual(30, service.CurrentIntervalSec);
        Assert.AreEqual(2, service.ConsecutiveFailures);
        Assert.AreEqual(3, delays.Count);
        Assert.AreEqual(TimeSpan.FromSeconds(30), delays[0]);
        Assert.AreEqual(TimeSpan.FromMilliseconds(25), delays[1]);
        Assert.AreEqual(TimeSpan.FromMilliseconds(50), delays[2]);

        // 另起一次在线会话后主动停止，应触发 POST /api/client/offline
        var beatCount2 = 0;
        var secondBeatDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var api2 = new DreamPortApiClient("https://xmcraft.cn", new StubHttpHandler((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/client/heartbeat")
            {
                Interlocked.Increment(ref beatCount2);
                secondBeatDone.TrySetResult();
                return JsonResponse(HttpStatusCode.OK, new { success = true, data = new { nextIntervalSec = 60 } });
            }
            if (req.RequestUri.AbsolutePath == "/api/client/offline")
            {
                Interlocked.Increment(ref offlineCalled);
                return JsonResponse(HttpStatusCode.OK, new { ok = true });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        api2.SetToken("live-token-2");

        await using var service2 = new DreamPortHeartbeatService(
            getClient: () => api2,
            isLoggedIn: () => true,
            ownsClientInstances: false,
            delayAsync: async (delay, ct) => await Task.Delay(TimeSpan.FromMinutes(1), ct));

        service2.Start("logout-test");
        await secondBeatDone.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await service2.StopAndNotifyOfflineAsync(TimeSpan.FromSeconds(2));

        Assert.IsFalse(service2.IsRunning);
        Assert.AreEqual(1, beatCount2);
        Assert.AreEqual(1, offlineCalled);
    }

    [TestMethod]
    public async Task CheckForUpdateAsync_UsesValidMirrorFirst_AndFallsBackToGitHubOnInvalidManifest()
    {
        var mirrorSha = new string('a', 64);
        var githubSha = new string('b', 64);
        var githubRequested = 0;

        var validMirrorJson = JsonSerializer.Serialize(new
        {
            schema = 1,
            product = "pcl-dreamport",
            version = "0.2.0",
            tag = "v0.2.0",
            notes = "Mirror release notes",
            publishedAt = "2026-11-25T00:00:00Z",
            assets = new[]
            {
                new
                {
                    name = "PCL-DreamPort-0.2.0-mac-arm64.zip",
                    platform = "mac-arm64",
                    size = 1234567L,
                    sha256 = mirrorSha,
                    url = "https://dl.xmcraft.cn/pcl-dreamport/v0.2.0/PCL-DreamPort-0.2.0-mac-arm64.zip"
                }
            }
        });

        using (var httpMirrorOk = new HttpClient(new StubHttpHandler((req, _) =>
        {
            if (req.RequestUri!.Host == "dl.xmcraft.cn")
                return StringResponse(HttpStatusCode.OK, validMirrorJson, "application/json");
            Interlocked.Increment(ref githubRequested);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        })))
        {
            var res = await DreamPortUpdateService.CheckForUpdateAsync(
                DreamPortUpdateChannel.Stable,
                "mac-arm64",
                "0.1.0",
                httpClient: httpMirrorOk,
                mirrorBaseUrl: "https://dl.xmcraft.cn",
                repoUrl: "https://github.com/Xiameng/PCL-DreamPort");

            Assert.IsTrue(res.HasUpdate);
            Assert.AreEqual("mirror", res.Source);
            Assert.AreEqual("0.2.0", res.LatestVersion);
            Assert.AreEqual(mirrorSha, res.Sha256);
            Assert.AreEqual(0, githubRequested, "镜像清单合规时不应回落请求 GitHub API");
        }

        // 镜像返回 http:// 非 HTTPS 链接或错误 product，必须回落 GitHub Releases + SHA256SUMS
        var badMirrorCases = new[]
        {
            """{"schema":2,"product":"pcl-dreamport","version":"0.2.0","tag":"v0.2.0","assets":[{"name":"a.zip","platform":"mac-arm64","size":10,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","url":"https://dl.xmcraft.cn/a.zip"}]}""",
            """{"schema":1,"product":"other-app","version":"0.2.0","tag":"v0.2.0","assets":[{"name":"a.zip","platform":"mac-arm64","size":10,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","url":"https://dl.xmcraft.cn/a.zip"}]}""",
            """{"schema":1,"product":"pcl-dreamport","version":"0.2.0","tag":"v0.2.0","assets":[{"name":"a.zip","platform":"mac-arm64","size":10,"sha256":"not-hex","url":"https://dl.xmcraft.cn/a.zip"}]}""",
            """{"schema":1,"product":"pcl-dreamport","version":"0.2.0","tag":"v0.2.0","assets":[{"name":"a.zip","platform":"mac-arm64","size":10,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","url":"http://dl.xmcraft.cn/a.zip"}]}""",
            "{broken-json"
        };

        var ghReleasesJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                tag_name = "v0.3.0",
                draft = false,
                prerelease = false,
                body = "GitHub release notes",
                published_at = "2026-11-25T01:00:00Z",
                assets = new object[]
                {
                    new
                    {
                        name = "PCL-DreamPort-0.3.0-mac-arm64.zip",
                        size = 2048L,
                        browser_download_url = "https://github.com/Xiameng/PCL-DreamPort/releases/download/v0.3.0/PCL-DreamPort-0.3.0-mac-arm64.zip"
                    },
                    new
                    {
                        name = "SHA256SUMS",
                        size = 128L,
                        browser_download_url = "https://github.com/Xiameng/PCL-DreamPort/releases/download/v0.3.0/SHA256SUMS"
                    }
                }
            }
        });

        foreach (var badManifest in badMirrorCases)
        {
            using var httpFallback = new HttpClient(new StubHttpHandler((req, _) =>
            {
                if (req.RequestUri!.AbsolutePath == "/pcl-dreamport/latest.json")
                    return StringResponse(HttpStatusCode.OK, badManifest, "application/json");
                if (req.RequestUri.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal))
                    return StringResponse(HttpStatusCode.OK, ghReleasesJson, "application/json");
                if (req.RequestUri.AbsolutePath.EndsWith("/SHA256SUMS", StringComparison.Ordinal))
                    return StringResponse(HttpStatusCode.OK, $"{githubSha} *PCL-DreamPort-0.3.0-mac-arm64.zip\n", "text/plain");
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));

            var res = await DreamPortUpdateService.CheckForUpdateAsync(
                DreamPortUpdateChannel.Stable,
                "mac-arm64",
                "0.1.0",
                httpClient: httpFallback,
                mirrorBaseUrl: "https://dl.xmcraft.cn",
                repoUrl: "https://github.com/Xiameng/PCL-DreamPort");

            Assert.IsTrue(res.HasUpdate);
            Assert.AreEqual("github", res.Source);
            Assert.AreEqual("0.3.0", res.LatestVersion);
            Assert.AreEqual(githubSha, res.Sha256);
        }
    }

    [TestMethod]
    public async Task CheckForUpdateAsync_HandlesGitHub404EmptyBetaChannelAndMissingSha256()
    {
        // 404 与空列表均安静返回 HasUpdate=false，不抛异常
        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.OK })
        {
            using var httpEmpty = new HttpClient(new StubHttpHandler((req, _) =>
            {
                if (req.RequestUri!.Host == "dl.xmcraft.cn")
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                return status == HttpStatusCode.NotFound
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : StringResponse(HttpStatusCode.OK, "[]", "application/json");
            }));

            var res = await DreamPortUpdateService.CheckForUpdateAsync(
                DreamPortUpdateChannel.Stable,
                "mac-arm64",
                "0.1.0",
                httpClient: httpEmpty,
                mirrorBaseUrl: "https://dl.xmcraft.cn",
                repoUrl: "https://github.com/Xiameng/PCL-DreamPort");

            Assert.IsFalse(res.HasUpdate);
        }

        // Beta vs Stable 过滤 & 仅含 x64 包时 arm64 不应跨架构匹配
        var ghJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                tag_name = "v0.4.0-beta.1",
                draft = false,
                prerelease = true,
                body = "Beta notes",
                published_at = "2026-11-25T02:00:00Z",
                assets = new[]
                {
                    new
                    {
                        name = "PCL-DreamPort-0.4.0-beta.1-mac-x64.zip",
                        size = 1000L,
                        browser_download_url = "https://github.com/Xiameng/PCL-DreamPort/releases/download/v0.4.0-beta.1/PCL-DreamPort-0.4.0-beta.1-mac-x64.zip"
                    }
                }
            },
            new
            {
                tag_name = "v0.3.5",
                draft = false,
                prerelease = false,
                body = "Stable notes",
                published_at = "2026-11-24T00:00:00Z",
                assets = new[]
                {
                    new
                    {
                        name = "PCL-DreamPort-0.3.5-mac-arm64.zip",
                        size = 1000L,
                        browser_download_url = "https://github.com/Xiameng/PCL-DreamPort/releases/download/v0.3.5/PCL-DreamPort-0.3.5-mac-arm64.zip"
                    }
                }
            }
        });

        using var httpChannel = new HttpClient(new StubHttpHandler((req, _) =>
        {
            if (req.RequestUri!.Host == "dl.xmcraft.cn")
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            return StringResponse(HttpStatusCode.OK, ghJson, "application/json");
        }));

        var stableArm64 = await DreamPortUpdateService.CheckForUpdateAsync(
            DreamPortUpdateChannel.Stable,
            "mac-arm64",
            "0.1.0",
            httpClient: httpChannel);
        Assert.IsTrue(stableArm64.HasUpdate);
        Assert.AreEqual("0.3.5", stableArm64.LatestVersion);
        Assert.AreEqual("PCL-DreamPort-0.3.5-mac-arm64.zip", stableArm64.AssetName);
        Assert.IsNull(stableArm64.Sha256, "缺少 SHA256SUMS 时 sha256 必须为 null");

        var betaArm64 = await DreamPortUpdateService.CheckForUpdateAsync(
            DreamPortUpdateChannel.Beta,
            "mac-arm64",
            "0.1.0",
            httpClient: httpChannel);
        Assert.IsTrue(betaArm64.HasUpdate);
        Assert.AreEqual("0.4.0-beta.1", betaArm64.LatestVersion);
        Assert.IsNull(betaArm64.DownloadUrl, "mac-arm64 不应错误匹配仅包含 mac-x64 的资产");
    }

    [TestMethod]
    public void PlatformMatching_Sha256Sums_CompareVersions_AndSkippedStore_BehaveStrictly()
    {
        Assert.AreEqual("mac-arm64", DreamPortUpdateService.CurrentPlatformTag(OSPlatform.OSX, Architecture.Arm64));
        Assert.AreEqual("mac-x64", DreamPortUpdateService.CurrentPlatformTag(OSPlatform.OSX, Architecture.X64));
        Assert.AreEqual("win-x64", DreamPortUpdateService.CurrentPlatformTag(OSPlatform.Windows, Architecture.X64));
        Assert.AreEqual("win-arm64", DreamPortUpdateService.CurrentPlatformTag(OSPlatform.Windows, Architecture.Arm64));
        Assert.AreEqual("linux-x64", DreamPortUpdateService.CurrentPlatformTag(OSPlatform.Linux, Architecture.X64));
        Assert.AreEqual("linux-arm64", DreamPortUpdateService.CurrentPlatformTag(OSPlatform.Linux, Architecture.Arm64));

        Assert.AreEqual("mac-arm64", DreamPortUpdateService.DetectGitHubAssetPlatform("PCL-DreamPort-1.0.0-mac-arm64.dmg"));
        Assert.AreEqual("mac-x64", DreamPortUpdateService.DetectGitHubAssetPlatform("PCL-DreamPort-1.0.0-osx-x86_64.zip"));
        Assert.AreEqual("win-x64", DreamPortUpdateService.DetectGitHubAssetPlatform("PCL-DreamPort-1.0.0-win-x64.zip"));
        Assert.AreEqual("win-arm64", DreamPortUpdateService.DetectGitHubAssetPlatform("PCL-DreamPort-1.0.0-windows-aarch64.zip"));
        Assert.AreEqual("linux-x64", DreamPortUpdateService.DetectGitHubAssetPlatform("PCL-DreamPort-1.0.0-linux-amd64.tar.gz"));
        Assert.AreEqual("linux-arm64", DreamPortUpdateService.DetectGitHubAssetPlatform("PCL-DreamPort-1.0.0-linux-arm64.tar.gz"));
        Assert.IsNull(DreamPortUpdateService.DetectGitHubAssetPlatform("SHA256SUMS"));
        Assert.IsNull(DreamPortUpdateService.DetectGitHubAssetPlatform("PCL-DreamPort-1.0.0-mac.dmg"));

        var expectedHash = new string('c', 64);
        var sumsContent = $"""
            {new string('0', 64)}  PCL-DreamPort-1.0.0-win-x64.zip
            {expectedHash.ToUpperInvariant()} *PCL-DreamPort-1.0.0-mac-arm64.zip
            """;
        Assert.AreEqual(expectedHash, DreamPortUpdateService.ParseSha256Sums(sumsContent, "pcl-dreamport-1.0.0-mac-arm64.zip"));
        Assert.IsNull(DreamPortUpdateService.ParseSha256Sums(sumsContent, "missing.zip"));

        Assert.IsTrue(DreamPortUpdateService.CompareVersions("0.2.0", "0.1.9") > 0);
        Assert.IsTrue(DreamPortUpdateService.CompareVersions("1.0.0", "1.0.0-beta.1") > 0);
        Assert.IsTrue(DreamPortUpdateService.CompareVersions("v1.0.0-beta.2", "1.0.0-beta.1") > 0);
        Assert.AreEqual(0, DreamPortUpdateService.CompareVersions("v0.2.0", "0.2.0"));

        var tempDir = Path.Combine(Path.GetTempPath(), $"dp-skip-test-{Guid.NewGuid():N}");
        try
        {
            var store = new DreamPortSkippedVersionStore(tempDir);
            store.SkipVersion(DreamPortUpdateChannel.Stable, "v0.2.0");
            store.SkipVersion(DreamPortUpdateChannel.Beta, "0.3.0-beta.1");

            var reloaded = new DreamPortSkippedVersionStore(tempDir);
            Assert.IsTrue(reloaded.IsVersionSkipped(DreamPortUpdateChannel.Stable, "0.2.0"));
            Assert.IsFalse(reloaded.IsVersionSkipped(DreamPortUpdateChannel.Beta, "0.2.0"));
            Assert.IsTrue(reloaded.IsVersionSkipped(DreamPortUpdateChannel.Beta, "v0.3.0-beta.1"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task DownloadAndVerifyAsync_AndMacOsDittoScript_EnforceSha256CleanupAndDitto()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dp-upd-dl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var payload = Encoding.UTF8.GetBytes("PCL-DreamPort-Binary-Payload-v0.2.0");
            var goodHash = Convert.ToHexStringLower(SHA256.HashData(payload));
            var badHash = new string('d', 64);

            using var http = new HttpClient(new StubHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            }));

            var okTarget = Path.Combine(tempDir, "good.zip");
            var okRes = await DreamPortUpdateService.DownloadAndVerifyAsync(http, "https://dl.xmcraft.cn/good.zip", okTarget, goodHash);
            Assert.IsTrue(okRes.Ok);
            Assert.IsTrue(File.Exists(okTarget));
            Assert.IsFalse(File.Exists(okTarget + ".dp-update-part"));

            var badTarget = Path.Combine(tempDir, "tampered.zip");
            var badRes = await DreamPortUpdateService.DownloadAndVerifyAsync(http, "https://dl.xmcraft.cn/bad.zip", badTarget, badHash);
            Assert.IsFalse(badRes.Ok);
            Assert.IsFalse(File.Exists(badTarget));
            Assert.IsFalse(File.Exists(badTarget + ".dp-update-part"), "校验失败后必须删除 .dp-update-part 临时文件");

            var missingHashRes = await DreamPortUpdateService.DownloadAndVerifyAsync(http, "https://dl.xmcraft.cn/nohash.zip", badTarget, null);
            Assert.IsFalse(missingHashRes.Ok);

            var script = DreamPortUpdateService.BuildMacOsDittoReplaceScript(
                12345,
                "/tmp/Staging/PCL-DreamPort.app",
                "/Applications/PCL-DreamPort.app");
            Assert.IsTrue(script.Contains("ditto '/tmp/Staging/PCL-DreamPort.app' '/Applications/PCL-DreamPort.app'", StringComparison.Ordinal));
            Assert.IsTrue(script.Contains("open -n '/Applications/PCL-DreamPort.app'", StringComparison.Ordinal));
            Assert.IsFalse(script.Contains("cp -R", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode code, object body)
        => StringResponse(code, JsonSerializer.Serialize(body), "application/json");

    private static HttpResponseMessage StringResponse(HttpStatusCode code, string content, string mediaType)
        => new(code)
        {
            Content = new StringContent(content, Encoding.UTF8, mediaType)
        };

    private sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request, cancellationToken));
    }
}
