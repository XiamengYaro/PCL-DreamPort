using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DreamPort.Core.Api;
using DreamPort.Core.Auth;
using DreamPort.Core.ServerPack;
using DreamPort.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamPort.Test;

[TestClass]
public sealed class DreamPortServerPackTests
{
    [TestMethod]
    public void IsSafePackPath_AndCompatibilityChecks_RejectUnsafeOrIncompatibleInputs()
    {
        Assert.IsTrue(DreamPortServerPackService.IsSafePackPath("mods/sodium.jar", "MyPack"));
        Assert.IsTrue(DreamPortServerPackService.IsSafePackPath("config/sodium-options.json", "MyPack"));

        Assert.IsFalse(DreamPortServerPackService.IsSafePackPath("", "MyPack"));
        Assert.IsFalse(DreamPortServerPackService.IsSafePackPath("../evil.jar", "MyPack"));
        Assert.IsFalse(DreamPortServerPackService.IsSafePackPath("mods\\evil.jar", "MyPack"));
        Assert.IsFalse(DreamPortServerPackService.IsSafePackPath("/etc/passwd", "MyPack"));
        Assert.IsFalse(DreamPortServerPackService.IsSafePackPath("C:/evil.jar", "MyPack"));
        Assert.IsFalse(DreamPortServerPackService.IsSafePackPath("versions/1.21.4/a.json", "MyPack"));
        Assert.IsFalse(DreamPortServerPackService.IsSafePackPath(".dp-serverpack.json", "MyPack"));
        Assert.IsFalse(DreamPortServerPackService.IsSafePackPath(".dp-state.json", "MyPack"));
        Assert.IsFalse(DreamPortServerPackService.IsSafePackPath("MyPack.json", "MyPack"));
        Assert.IsFalse(DreamPortServerPackService.IsSafePackPath("MyPack.jar", "MyPack"));

        Assert.IsTrue(DreamPortServerPackService.CheckCompatibility("1.21.4", ["fabric"], "1.21.4", "Fabric").Compatible);
        Assert.IsTrue(DreamPortServerPackService.CheckCompatibility("", [], "1.20.1", "原版").Compatible);
        Assert.IsFalse(DreamPortServerPackService.CheckCompatibility("1.21.4", ["fabric"], "1.20.1", "fabric").Compatible);
        Assert.IsFalse(DreamPortServerPackService.CheckCompatibility("1.21.4", ["neoforge"], "1.21.4", "fabric").Compatible);
    }

    [TestMethod]
    public async Task ServerPackSync_HandlesDirectFiles_PlayerOwnMods_DisabledSkip_CorruptState_AndModrinthDualSource()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dp-sp-test-" + Guid.NewGuid().ToString("N"));
        var instDir = Path.Combine(tempDir, "inst-1");
        Directory.CreateDirectory(Path.Combine(instDir, "mods"));

        try
        {
            var serverModBytes = Encoding.UTF8.GetBytes("SERVER-MOD-A-V1");
            var serverModSha = Convert.ToHexStringLower(SHA256.HashData(serverModBytes));

            var configBytes = Encoding.UTF8.GetBytes("{\"port\":25565}");
            var configSha = Convert.ToHexStringLower(SHA256.HashData(configBytes));

            var optionalBytes = Encoding.UTF8.GetBytes("OPTIONAL-MOD");
            var optionalSha = Convert.ToHexStringLower(SHA256.HashData(optionalBytes));

            var mrJarBytes = Encoding.UTF8.GetBytes("MODRINTH-JAR-BYTES");
            var mrSha512 = Convert.ToHexStringLower(SHA512.HashData(mrJarBytes));

            // Player's self-installed mod + explicitly disabled mod
            var playerOwnPath = Path.Combine(instDir, "mods", "player-own.jar");
            await File.WriteAllTextAsync(playerOwnPath, "PLAYER-CUSTOM");
            var disabledMarker = Path.Combine(instDir, "mods", "optional.jar.disabled");
            await File.WriteAllTextAsync(disabledMarker, "DISABLED");

            var unsafeManifest = new DreamPortServerPackManifestResult(
                Ok: true, Version: "1.0", GeneratedAtMs: 1, GameVersion: "1.21.4", Loaders: ["fabric"],
                ModrinthMods: [],
                Files: [new DreamPortServerPackFile("../escape.jar", serverModSha, serverModBytes.Length)],
                TotalBytes: serverModBytes.Length);
            var rejectedPlan = await DreamPortServerPackService.PlanSyncAsync(unsafeManifest, instDir, "inst-1");
            Assert.HasCount(1, rejectedPlan.Rejected);

            var handler = new StubHttpHandler((req, _) =>
            {
                var uri = req.RequestUri!;
                if (uri.Host == "xmcraft.cn")
                {
                    return uri.AbsolutePath switch
                    {
                        "/api/client/serverpack" => JsonResponse(HttpStatusCode.OK, new
                        {
                            success = true,
                            data = new
                            {
                                version = "v2",
                                generatedAt = 1700000000000L,
                                gameVersion = "1.21.4",
                                loaders = new[] { "fabric" },
                                modrinthMods = new[]
                                {
                                    new { projectId = "AANobbMI", slug = "sodium", title = "Sodium", versionId = "ver123", required = true }
                                },
                                files = new object[]
                                {
                                    new { path = "mods/server-a.jar", sha256 = serverModSha, size = serverModBytes.Length },
                                    new { path = "mods/optional.jar", sha256 = optionalSha, size = optionalBytes.Length },
                                    new { path = "config/dp.json", sha256 = configSha, size = configBytes.Length }
                                },
                                totalBytes = serverModBytes.Length + optionalBytes.Length + configBytes.Length
                            }
                        }),
                        "/api/client/serverpack/file/mods/server-a.jar" => BinaryResponse(serverModBytes),
                        "/api/client/serverpack/file/config/dp.json" => BinaryResponse(configBytes),
                        _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                    };
                }

                if (uri.Host == "api.modrinth.com" && uri.AbsolutePath == "/v2/version/ver123")
                {
                    return JsonResponse(HttpStatusCode.OK, new
                    {
                        id = "ver123",
                        files = new[]
                        {
                            new
                            {
                                filename = "sodium-fabric-0.6.jar",
                                url = "https://cdn.modrinth.com/data/AANobbMI/versions/ver123/sodium-fabric-0.6.jar",
                                primary = true,
                                size = mrJarBytes.Length,
                                hashes = new { sha512 = mrSha512 }
                            }
                        }
                    });
                }

                if (uri.Host == "cdn.modrinth.com")
                    return BinaryResponse(mrJarBytes);

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            using var api = new DreamPortApiClient("https://xmcraft.cn", handler, disposeHandler: false);
            using var sharedHttp = new HttpClient(handler, disposeHandler: false);

            var manifest = await api.GetServerPackManifestAsync();
            Assert.IsTrue(manifest.Ok);
            Assert.HasCount(3, manifest.Files);
            Assert.HasCount(1, manifest.ModrinthMods);

            var apply1 = await DreamPortServerPackService.ApplySyncAsync(
                api, manifest, instDir, "1.21.4", "fabric", "inst-1", modrinthHttpClient: sharedHttp);
            Assert.IsTrue(apply1.Ok, apply1.Message);
            Assert.AreEqual(2, apply1.DownloadedCount);
            Assert.AreEqual(0, apply1.DeletedCount);
            Assert.AreEqual(1, apply1.ModrinthInstalledCount);

            // Disabled file stays disabled & not downloaded; player-own mod stays untouched; sodium & direct files exist
            Assert.IsFalse(File.Exists(Path.Combine(instDir, "mods", "optional.jar")));
            Assert.IsTrue(File.Exists(disabledMarker));
            Assert.IsTrue(File.Exists(playerOwnPath));
            Assert.IsTrue(File.Exists(Path.Combine(instDir, "mods", "server-a.jar")));
            Assert.IsTrue(File.Exists(Path.Combine(instDir, "config", "dp.json")));
            Assert.IsTrue(File.Exists(Path.Combine(instDir, "mods", "sodium-fabric-0.6.jar")));

            // Second sync removes config/dp.json -> only managed config/dp.json deleted, player-own.jar kept!
            var shrinkManifest = manifest with
            {
                Version = "v3",
                Files = [new DreamPortServerPackFile("mods/server-a.jar", serverModSha, serverModBytes.Length)]
            };
            var apply2 = await DreamPortServerPackService.ApplySyncAsync(
                api, shrinkManifest, instDir, "1.21.4", "fabric", "inst-1", modrinthHttpClient: sharedHttp);
            Assert.IsTrue(apply2.Ok);
            Assert.AreEqual(1, apply2.DeletedCount);
            Assert.IsFalse(File.Exists(Path.Combine(instDir, "config", "dp.json")));
            Assert.IsTrue(File.Exists(playerOwnPath));

            // Hash tamper detection refuses corrupted bytes
            var badBytes = Encoding.UTF8.GetBytes("TAMPERED");
            var tamperHandler = new StubHttpHandler((_, _) => BinaryResponse(badBytes));
            using var tamperHttp = new HttpClient(tamperHandler);
            var missingNewFile = shrinkManifest with
            {
                Files = [.. shrinkManifest.Files, new DreamPortServerPackFile("mods/tampered.jar", serverModSha, badBytes.Length)]
            };
            var tamperRes = await DreamPortServerPackService.ApplySyncAsync(
                api, missingNewFile, instDir, "1.21.4", "fabric", "inst-1", modrinthHttpClient: tamperHttp);
            Assert.IsFalse(tamperRes.Ok);
            Assert.IsFalse(File.Exists(Path.Combine(instDir, "mods", "tampered.jar")));

            // Corrupted .dp-serverpack.json gets backed up and throws instead of wiping state
            await File.WriteAllTextAsync(Path.Combine(instDir, DreamPortServerPackService.StateFileName), "{bad-json");
            try
            {
                await DreamPortServerPackService.PlanSyncAsync(shrinkManifest, instDir, "inst-1");
                Assert.Fail("Expected InvalidDataException on corrupt state file");
            }
            catch (InvalidDataException)
            {
                var backups = Directory.GetFiles(instDir, ".dp-serverpack.json.corrupt.*");
                Assert.HasCount(1, backups);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task SyncViewModel_HardBlocksIncompatibleInstance_AndSyncsCompatibleInstance()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "dp-sync-vm-" + Guid.NewGuid().ToString("N"));
        var instDir = Path.Combine(tempDir, "inst-vm");
        Directory.CreateDirectory(instDir);
        var fileBytes = Encoding.UTF8.GetBytes("PACK-FILE-CONTENT");
        var fileSha = Convert.ToHexStringLower(SHA256.HashData(fileBytes));

        try
        {
            var storeDir = Path.Combine(tempDir, "store");
            Directory.CreateDirectory(storeDir);
            var store = new DreamPortSessionStore(storeDir, new InMemoryVault());
            var loader = "vanilla";
            var ver = "1.20.1";

            var handler = new StubHttpHandler((req, _) =>
            {
                var path = req.RequestUri!.AbsolutePath;
                return path switch
                {
                    "/api/client/serverpack" => JsonResponse(HttpStatusCode.OK, new
                    {
                        success = true,
                        data = new
                        {
                            version = "2.0",
                            generatedAt = 1000L,
                            gameVersion = "1.21.4",
                            loaders = new[] { "fabric" },
                            modrinthMods = Array.Empty<object>(),
                            files = new[] { new { path = "mods/pack.jar", sha256 = fileSha, size = fileBytes.Length } },
                            totalBytes = fileBytes.Length
                        }
                    }),
                    "/api/client/serverpack/file/mods/pack.jar" => BinaryResponse(fileBytes),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                };
            });

            using var sharedHttp = new HttpClient(handler, disposeHandler: false);
            await using var vm = new DreamPortSyncViewModel(
                store: store,
                clientFactory: url => new DreamPortApiClient(url, handler, disposeHandler: false),
                getSelectedInstance: () => (instDir, "inst-vm", ver, loader),
                httpClient: sharedHttp);

            // Incompatible (1.20.1 vanilla vs 1.21.4 fabric) -> hard block
            await vm.InitializeOrSyncAsync();
            Assert.IsFalse(vm.IsCompatible);
            Assert.IsFalse(vm.CanApply);
            Assert.IsTrue(vm.HasCompatibilityWarning);

            // Switch instance to compatible 1.21.4 fabric -> plan + apply succeed
            ver = "1.21.4";
            loader = "fabric";
            await vm.CheckServerPackCommand.ExecuteAsync(null);
            Assert.IsTrue(vm.IsCompatible);
            Assert.IsTrue(vm.CanApply);
            Assert.HasCount(1, vm.PendingFileActions);

            await vm.ApplyServerPackCommand.ExecuteAsync(null);
            Assert.IsTrue(File.Exists(Path.Combine(instDir, "mods", "pack.jar")));
            Assert.HasCount(0, vm.PendingFileActions);
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

    private static HttpResponseMessage BinaryResponse(byte[] bytes)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
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
