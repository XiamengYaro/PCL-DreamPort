using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DreamPort.Core.Api;
using DreamPort.Core.CloudSave;
using DreamPort.Core.Diagnostics;
using DreamPort.Core.ProfileSync;
using DreamPort.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App;

namespace DreamPort.Test;

[TestClass]
public sealed class DreamPortPhase5Tests
{
    [TestMethod]
    public async Task CloudSave_UploadResumeDuplicateAndSafeNonOverwritingRestore_WorkCorrectly()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"dp-cs-test-{Guid.NewGuid():N}");
        var instDir = Path.Combine(tempRoot, "instances", "Survival");
        var worldDir = Path.Combine(instDir, "saves", "MyWorld");
        Directory.CreateDirectory(worldDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(worldDir, "session.lock"), "locked", Encoding.UTF8);
            var levelBytes = Encoding.UTF8.GetBytes("LEVEL_DAT_CONTENT_" + new string('X', 32));
            await File.WriteAllBytesAsync(Path.Combine(worldDir, "level.dat"), levelBytes);
            Directory.CreateDirectory(Path.Combine(worldDir, "region"));
            await File.WriteAllBytesAsync(Path.Combine(worldDir, "region", "r.0.0.mca"), Encoding.UTF8.GetBytes("MCA_DATA"));

            var listed = DreamPortCloudSaveService.ListLocalWorlds(instDir);
            Assert.HasCount(1, listed);
            Assert.AreEqual("MyWorld", listed[0].WorldName);

            var testZip = Path.Combine(tempRoot, "check.zip");
            var (zipSize, zipSha) = await DreamPortCloudSaveService.CreateWorldZipAsync(worldDir, testZip);
            Assert.IsTrue(zipSize > 0);
            Assert.AreEqual(64, zipSha.Length);
            using (var checkArc = ZipFile.OpenRead(testZip))
            {
                Assert.IsFalse(checkArc.Entries.Any(e => e.FullName.Contains("session.lock", StringComparison.OrdinalIgnoreCase)));
                Assert.IsTrue(checkArc.Entries.Any(e => e.FullName.EndsWith("level.dat", StringComparison.OrdinalIgnoreCase)));
            }

            // 模拟分块续传（chunk 0 已在服务端，仅需上传 chunk 1..n）
            var uploadedChunkIndices = new List<int>();
            var completedCalled = false;
            var renameCalled = false;
            var deleteCalled = false;
            var duplicateMode = false;
            var tamperedFile = false;

            using var api = new DreamPortApiClient("https://xmcraft.cn", new StubHttpHandler((req, _) =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (req.Method == HttpMethod.Post && path == "/api/client/save-backups/begin")
                {
                    if (duplicateMode)
                    {
                        return JsonResponse(HttpStatusCode.OK, new
                        {
                            success = true,
                            data = new
                            {
                                id = 42L,
                                instanceName = "Survival",
                                worldName = "MyWorld",
                                displayName = "MyWorld",
                                sizeBytes = zipSize,
                                sha256 = zipSha,
                                chunkSize = 100,
                                chunksTotal = 1,
                                status = "ready",
                                duplicate = true,
                                uploadedChunks = Array.Empty<int>()
                            }
                        });
                    }

                    var chunkSize = 80;
                    var chunksTotal = (int)((zipSize + chunkSize - 1) / chunkSize);
                    return JsonResponse(HttpStatusCode.OK, new
                    {
                        success = true,
                        data = new
                        {
                            id = 101L,
                            instanceName = "Survival",
                            worldName = "MyWorld",
                            displayName = "Before矿洞",
                            sizeBytes = zipSize,
                            sha256 = zipSha,
                            chunkSize,
                            chunksTotal,
                            status = "uploading",
                            duplicate = false,
                            uploadedChunks = new[] { 0 }
                        }
                    });
                }

                if (req.Method == HttpMethod.Post && path.StartsWith("/api/client/save-backups/101/chunks/", StringComparison.Ordinal))
                {
                    var idxStr = path.Split('/')[^1];
                    var idx = int.Parse(idxStr);
                    uploadedChunkIndices.Add(idx);
                    var bytes = req.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    Assert.IsTrue(bytes.Length > 0);
                    return JsonResponse(HttpStatusCode.OK, new { success = true, data = new { chunk = idx, uploaded = true } });
                }

                if (req.Method == HttpMethod.Post && path == "/api/client/save-backups/101/complete")
                {
                    completedCalled = true;
                    return JsonResponse(HttpStatusCode.OK, new
                    {
                        success = true,
                        data = new
                        {
                            id = 101L,
                            instanceName = "Survival",
                            worldName = "MyWorld",
                            displayName = "Before矿洞",
                            sizeBytes = zipSize,
                            sha256 = zipSha,
                            chunkSize = 80,
                            chunksTotal = 2,
                            status = "ready"
                        }
                    });
                }

                if (req.Method == HttpMethod.Get && path == "/api/client/save-backups/101")
                {
                    return JsonResponse(HttpStatusCode.OK, new
                    {
                        success = true,
                        data = new
                        {
                            id = 101L,
                            instanceName = "Survival",
                            worldName = "MyWorld",
                            displayName = "Before矿洞",
                            sizeBytes = zipSize,
                            sha256 = zipSha,
                            chunkSize = 80,
                            chunksTotal = 2,
                            status = "ready"
                        }
                    });
                }

                if (req.Method == HttpMethod.Get && path == "/api/client/save-backups/101/file")
                {
                    var zipBytes = tamperedFile
                        ? Encoding.UTF8.GetBytes("TAMPERED_SAVE_BYTES")
                        : File.ReadAllBytes(testZip);
                    var resp = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(zipBytes)
                    };
                    resp.Headers.TryAddWithoutValidation("X-Content-SHA256", zipSha);
                    return resp;
                }

                if (req.Method == HttpMethod.Put && path == "/api/client/save-backups/101")
                {
                    renameCalled = true;
                    return JsonResponse(HttpStatusCode.OK, new
                    {
                        success = true,
                        data = new
                        {
                            id = 101L,
                            instanceName = "Survival",
                            worldName = "MyWorld",
                            displayName = "新命名存档",
                            sizeBytes = zipSize,
                            sha256 = zipSha,
                            status = "ready"
                        }
                    });
                }

                if (req.Method == HttpMethod.Delete && path == "/api/client/save-backups/101")
                {
                    deleteCalled = true;
                    return JsonResponse(HttpStatusCode.OK, new { success = true, message = "云备份已删除" });
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            api.SetToken("cs-token");

            var upRes = await DreamPortCloudSaveService.UploadWorldAsync(
                api, instDir, "Survival", "MyWorld", "1.21.4", "Before矿洞", "测试备注");
            Assert.IsTrue(upRes.Ok, upRes.Message);
            Assert.IsFalse(upRes.Duplicate);
            Assert.IsTrue(completedCalled);
            Assert.IsFalse(uploadedChunkIndices.Contains(0), "已上传的 chunk 0 应断点续传跳过");
            Assert.IsTrue(uploadedChunkIndices.Contains(1));

            duplicateMode = true;
            var dupRes = await DreamPortCloudSaveService.UploadWorldAsync(api, instDir, "Survival", "MyWorld");
            Assert.IsTrue(dupRes.Ok);
            Assert.IsTrue(dupRes.Duplicate);

            // 验证恢复：同名本地目录已存在，必须创建带时间戳的新目录而绝不覆盖原存档
            var restoreRes = await DreamPortCloudSaveService.RestoreBackupAsync(api, instDir, 101L, "MyWorld");
            Assert.IsTrue(restoreRes.Ok, restoreRes.Message);
            Assert.AreNotEqual("MyWorld", restoreRes.RestoredWorldName);
            Assert.IsTrue(restoreRes.RestoredWorldName!.StartsWith("MyWorld-", StringComparison.Ordinal));
            Assert.IsTrue(File.Exists(Path.Combine(restoreRes.TargetDirectory!, "level.dat")));
            Assert.IsTrue(File.Exists(Path.Combine(restoreRes.TargetDirectory!, "region", "r.0.0.mca")));
            Assert.IsFalse(Directory.EnumerateFiles(Path.Combine(instDir, "saves"), "*.dp-save-part", SearchOption.AllDirectories).Any());

            // 校验损坏 / 篡改 SHA-256 必须拒绝恢复且清理 .dp-save-part
            tamperedFile = true;
            var directTamperedZip = Path.Combine(tempRoot, "tampered-direct.zip");
            var directTamperedRes = await api.DownloadCloudSaveToFileAsync(101L, directTamperedZip, zipSha);
            Assert.IsFalse(directTamperedRes.Ok);
            Assert.IsFalse(File.Exists(directTamperedZip));
            Assert.IsFalse(File.Exists(directTamperedZip + ".dp-save-part"), "校验失败后必须删除 .dp-save-part 临时文件");

            var tamperedRestore = await DreamPortCloudSaveService.RestoreBackupAsync(api, instDir, 101L, "TamperedWorld");
            Assert.IsFalse(tamperedRestore.Ok);
            Assert.IsFalse(Directory.Exists(Path.Combine(instDir, "saves", "TamperedWorld")));
            tamperedFile = false;

            var renameRes = await api.RenameCloudSaveAsync(101L, "新命名存档");
            Assert.IsTrue(renameRes.Ok);
            Assert.AreEqual("新命名存档", renameRes.Item?.DisplayName);
            Assert.IsTrue(renameCalled);

            var delRes = await api.DeleteCloudSaveAsync(101L);
            Assert.IsTrue(delRes.Ok);
            Assert.IsTrue(deleteCalled);

            // 验证 Zip-Slip 拦截
            var evilZip = Path.Combine(tempRoot, "zipslip.zip");
            using (var fs = new FileStream(evilZip, FileMode.Create, FileAccess.Write))
            using (var arc = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var entry = arc.CreateEntry("../../pwned.txt");
                using var ew = new StreamWriter(entry.Open());
                ew.Write("evil");
            }
            var extractOut = Path.Combine(tempRoot, "extract-out");
            Assert.Throws<InvalidDataException>(() =>
                DreamPortCloudSaveService.ExtractZipSafely(evilZip, extractOut));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ProfileSync_EnforcesWhitelistPrivacy_ValidatesThemes_AndHandlesOptimisticLockConflict()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dp-ps-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var localProfile = CreateSampleProfile(ColorTheme.CatBlue, 854, 480);
            var serialized = DreamPortProfileSyncService.SerializePayload(localProfile);
            var json = serialized.ToJsonString();

            // 白名单约束：严禁包含任何凭据/Key/代理/Java 路径/JVM 参数字段
            foreach (var forbidden in new[]
                     {
                         "Profiles", "MirrorChyanKey", "CustomAddress", "CustomUsername",
                         "CustomPassword", "SelectedJava", "JvmArgs", "GameArgs", "PreLaunchCommand", "token"
                     })
            {
                Assert.IsFalse(
                    json.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"白名单配置不得包含敏感字段 {forbidden}");
            }

            Assert.IsTrue(DreamPortProfileSyncService.TryParsePayload(json, out var roundTripped, out var err), err);
            Assert.AreEqual(854, roundTripped!.Launch.GameWindowWidth);
            Assert.IsFalse(DreamPortProfileSyncService.TryParsePayload("""{"schema":99}""", out _, out _));

            long remoteRevision = 0;
            string? remotePayloadJson = null;

            using var api = new DreamPortApiClient("https://xmcraft.cn", new StubHttpHandler((req, _) =>
            {
                if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/api/client/profile-sync")
                {
                    return JsonResponse(HttpStatusCode.OK, new
                    {
                        success = true,
                        data = new
                        {
                            enabled = true,
                            revision = remoteRevision,
                            payload = remotePayloadJson is null ? (object?)null : JsonDocument.Parse(remotePayloadJson).RootElement.Clone(),
                            clientVersion = "0.2.0",
                            updatedAt = 1764000100000L,
                            maxBytes = 256 * 1024
                        }
                    });
                }

                if (req.Method == HttpMethod.Put && req.RequestUri!.AbsolutePath == "/api/client/profile-sync")
                {
                    using var doc = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                    var baseRev = doc.RootElement.GetProperty("baseRevision").GetInt64();
                    var incomingPayload = doc.RootElement.GetProperty("payload").GetRawText();

                    if (baseRev != remoteRevision)
                    {
                        return JsonResponse(HttpStatusCode.Conflict, new
                        {
                            success = false,
                            message = "配置已被另一台设备更新",
                            data = new
                            {
                                enabled = true,
                                revision = remoteRevision,
                                payload = remotePayloadJson is null ? (object?)null : JsonDocument.Parse(remotePayloadJson).RootElement.Clone(),
                                clientVersion = "0.2.1-other",
                                updatedAt = 1764000200000L,
                                maxBytes = 256 * 1024
                            }
                        });
                    }

                    remoteRevision++;
                    remotePayloadJson = incomingPayload;
                    return JsonResponse(HttpStatusCode.OK, new
                    {
                        success = true,
                        data = new
                        {
                            enabled = true,
                            revision = remoteRevision,
                            payload = JsonDocument.Parse(remotePayloadJson).RootElement.Clone(),
                            clientVersion = "0.2.0",
                            updatedAt = 1764000300000L,
                            maxBytes = 256 * 1024
                        }
                    });
                }

                if (req.Method == HttpMethod.Delete && req.RequestUri!.AbsolutePath == "/api/client/profile-sync")
                {
                    remoteRevision = 0;
                    remotePayloadJson = null;
                    return JsonResponse(HttpStatusCode.OK, new { success = true, message = "云端配置已清除" });
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            api.SetToken("ps-token");

            var svc = new DreamPortProfileSyncService(
                dataDirectory: tempDir,
                capture: () => localProfile,
                apply: p => localProfile = p);

            var firstPush = await svc.PushAsync(api, force: false);
            Assert.IsTrue(firstPush.Ok);
            Assert.AreEqual(1L, firstPush.Status?.LocalRevision);

            // 模拟另一台设备抢先推了一版 (revision=2)
            remoteRevision = 2;
            var device2Profile = CreateSampleProfile(ColorTheme.DeathBlue, 1280, 720);
            remotePayloadJson = DreamPortProfileSyncService.SerializePayload(device2Profile).ToJsonString();

            var conflictPush = await svc.PushAsync(api, force: false);
            Assert.IsFalse(conflictPush.Ok);
            Assert.IsTrue(conflictPush.IsConflict);
            Assert.AreEqual(2L, conflictPush.ConflictRemoteRevision);

            // 显式选择拉取云端应用到本机
            var pulled = await svc.PullAsync(api);
            Assert.IsTrue(pulled.Ok);
            Assert.AreEqual(ColorTheme.DeathBlue, localProfile.Preference.DarkColor);
            Assert.AreEqual(1280, localProfile.Launch.GameWindowWidth);
            Assert.AreEqual(2L, pulled.Status?.LocalRevision);

            // 显式强制推送也正常工作
            remoteRevision = 5;
            localProfile = CreateSampleProfile(ColorTheme.HmclBlue, 1600, 900);
            var forcedPush = await svc.PushAsync(api, force: true);
            Assert.IsTrue(forcedPush.Ok);
            Assert.AreEqual(6L, forcedPush.Status?.LocalRevision);

            var reset = await svc.ResetAsync(api);
            Assert.IsTrue(reset.Ok);
            Assert.AreEqual(0L, remoteRevision);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task LogDiagnose_RedactsSecrets_BoundsLargeLog_AndPollsAsyncJob()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dp-log-test-{Guid.NewGuid():N}");
        var instDir = Path.Combine(tempDir, "inst");
        Directory.CreateDirectory(Path.Combine(instDir, "logs"));
        Directory.CreateDirectory(Path.Combine(instDir, "crash-reports"));
        Directory.CreateDirectory(Path.Combine(instDir, "mods"));
        try
        {
            // 造一个不应暴露文件名的 mod 文件与超长日志
            await File.WriteAllBytesAsync(Path.Combine(instDir, "mods", "secret-custom-mod-name.jar"), [1, 2, 3]);
            var lines = new List<string>();
            for (var i = 0; i < 900; i++)
            {
                lines.Add($"[10:00:{i % 60:D2}] [main/INFO]: Loading chunk index {i} for Setting user: Xia_Meng_ at /Users/xiameng/secret");
            }
            lines.Add("[10:15:01] [main/ERROR]: Failed auth token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTYifQ.abcdefg M.C512_BAY.0.U.LONGSECRETREFRESHTOKEN123456 C:\\Users\\xiameng\\AppData");
            await File.WriteAllLinesAsync(Path.Combine(instDir, "logs", "latest.log"), lines);

            var crashLines = new List<string>
            {
                "---- Minecraft Crash Report ----",
                "// Oops. Player Xia_Meng_ was here",
                "java.lang.OutOfMemoryError: Java heap space"
            };
            for (var i = 0; i < 420; i++)
                crashLines.Add($"\tat net.minecraft.server.Tick_{i}(SourceFile:{i})");
            await File.WriteAllLinesAsync(Path.Combine(instDir, "crash-reports", "crash-2026-11-25.txt"), crashLines);

            var payload = DreamPortLogDiagnoseService.BuildPayload(new DreamPortLogDiagnoseBuildInput(
                InstanceDir: instDir,
                InstanceName: "Survival",
                GameVersion: "1.21.4",
                Loader: "fabric",
                ModCount: 1,
                ExitCode: "1",
                RuntimeLines: ["Setting user: Xia_Meng_ extra Al keepword"],
                ExtraIdentities: ["Xia_Meng_", "Al"]));

            Assert.IsTrue(payload.Truncated);
            Assert.IsFalse(payload.Content.Contains("Xia_Meng_", StringComparison.Ordinal));
            Assert.IsFalse(payload.Content.Contains("/Users/xiameng", StringComparison.Ordinal));
            Assert.IsFalse(payload.Content.Contains(@"C:\Users\xiameng", StringComparison.Ordinal));
            Assert.IsFalse(payload.Content.Contains("eyJhbGciOiJIUzI1NiJ9", StringComparison.Ordinal));
            Assert.IsFalse(payload.Content.Contains("M.C512_BAY", StringComparison.Ordinal));
            // 仅 2 字符短词 "Al" 不得污染正常单词 "Failed"
            Assert.IsTrue(payload.Content.Contains("keepword", StringComparison.Ordinal));
            Assert.IsFalse(payload.ContextText.Contains("secret-custom-mod-name", StringComparison.Ordinal));
            Assert.IsTrue(payload.ContextText.Contains("模组数量：1", StringComparison.Ordinal));

            var pollCount = 0;
            using var api = new DreamPortApiClient("https://xmcraft.cn", new StubHttpHandler((req, _) =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (req.Method == HttpMethod.Post && path == "/api/ai/log-diagnose")
                {
                    return JsonResponse(HttpStatusCode.OK, new
                    {
                        success = true,
                        data = new { accepted = true, jobId = "job123456", pollAfterMs = 1500 }
                    });
                }

                if (req.Method == HttpMethod.Get && path == "/api/ai/log-diagnose/result")
                {
                    pollCount++;
                    if (pollCount == 1)
                    {
                        return JsonResponse(HttpStatusCode.OK, new
                        {
                            success = true,
                            data = new { found = true, status = "running" }
                        });
                    }

                    return JsonResponse(HttpStatusCode.OK, new
                    {
                        success = true,
                        data = new
                        {
                            found = true,
                            status = "done",
                            usage = new { totalTokens = 420, latencyMs = 3100 },
                            result = new
                            {
                                verdict = "likely_cause",
                                summary = "游戏最大堆内存不足导致崩溃。",
                                items = new[]
                                {
                                    new
                                    {
                                        title = "Java 堆内存溢出",
                                        evidence = "java.lang.OutOfMemoryError: Java heap space",
                                        advice = "在启动设置中将最大内存调至 6 GB 以上",
                                        confidence = 0.94
                                    }
                                },
                                askFor = new[] { "确认当前物理内存容量" }
                            }
                        }
                    });
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            api.SetToken("diag-token");

            var execRes = await DreamPortLogDiagnoseService.RunAsync(
                api,
                payload,
                delayAsync: (_, _) => Task.CompletedTask);
            Assert.IsTrue(execRes.Ok, execRes.Error);
            Assert.AreEqual(2, pollCount);
            Assert.AreEqual(420, execRes.TotalTokens);
            Assert.IsTrue(execRes.DisplayText.Contains("Java 堆内存溢出", StringComparison.Ordinal));
            Assert.IsTrue(execRes.DisplayText.Contains("已发现可能原因", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ResourceAi_TranslatesUpTo30Items_Explains_AndUsesMemoryCache()
    {
        var translateCalls = 0;
        var explainCalls = 0;

        using var api = new DreamPortApiClient("https://xmcraft.cn", new StubHttpHandler((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/ai/resource/translate")
            {
                translateCalls++;
                using var doc = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                var arr = doc.RootElement.GetProperty("items");
                Assert.IsTrue(arr.GetArrayLength() <= 30, "单次当页翻译不得超过 30 条");

                var dict = new Dictionary<string, object>();
                foreach (var it in arr.EnumerateArray())
                {
                    var id = it.GetProperty("id").GetString()!;
                    dict[id] = new
                    {
                        titleZh = $"中文标题_{id}",
                        summaryZh = $"中文简介_{id}"
                    };
                }

                return JsonResponse(HttpStatusCode.OK, new
                {
                    success = true,
                    data = new { translations = dict, cacheHits = 1 }
                });
            }

            if (path == "/api/ai/resource/explain")
            {
                explainCalls++;
                return JsonResponse(HttpStatusCode.OK, new
                {
                    success = true,
                    data = new
                    {
                        overview = "提供高清钠渲染与光影扩展的高性能客户端模组。",
                        features = new[] { "大幅提升帧率", "兼容现代光影管线" },
                        notes = new[] { "需要对应版本的 Fabric API" },
                        cached = false
                    }
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        api.SetToken("res-ai-token");

        var helper = new DreamPortResourceAiHelper();
        var items = Enumerable.Range(1, 35)
            .Select(i => new DreamPortResourceTranslateItem($"m{i}", $"Mod {i}", $"Summary {i}"))
            .ToArray();

        var first = await helper.TranslatePageAsync(api, items);
        Assert.IsTrue(first.Ok);
        Assert.HasCount(30, first.Translations);
        Assert.AreEqual(1, translateCalls);

        // 同一页再次调用全部命中本地内存缓存，不再发出 HTTP 请求
        var second = await helper.TranslatePageAsync(api, items);
        Assert.IsTrue(second.Ok);
        Assert.AreEqual(1, translateCalls);
        Assert.AreEqual(30, second.CacheHits);

        var explainReq = new DreamPortResourceExplainRequest(
            "mod", "Sodium", "Modern rendering engine", "Long description", ["optimization"], []);
        var exp1 = await helper.ExplainAsync(api, explainReq);
        Assert.IsTrue(exp1.Ok);
        Assert.IsFalse(exp1.Cached);
        Assert.AreEqual(1, explainCalls);

        var exp2 = await helper.ExplainAsync(api, explainReq);
        Assert.IsTrue(exp2.Ok);
        Assert.IsTrue(exp2.Cached);
        Assert.AreEqual(1, explainCalls);

        var formatted = DreamPortResourceAiHelper.FormatExplainForDisplay("Sodium", exp2);
        Assert.IsTrue(formatted.Contains("一句话定位", StringComparison.Ordinal));
        Assert.IsTrue(formatted.Contains("Fabric API", StringComparison.Ordinal));
    }

    private static DreamPortPortableProfile CreateSampleProfile(ColorTheme darkTheme, int width, int height)
    {
        return new DreamPortPortableProfile(
            Schema: DreamPortProfileSyncService.CurrentSchema,
            Update: new DreamPortPortableUpdateSettings(LauncherAutoUpdateBehavior.DownloadAndAnnounce, UpdateChannel.Release),
            Download: new DreamPortPortableDownloadSettings(63, 7, 42, DownloadHttpMode.Auto, 1, 1, true, true, true, 0, 1),
            Launch: new DreamPortPortableLaunchSettings(0, 15, 0, false, JvmPreferredIpStack.Default, LauncherVisibility.DoNothing, GameProcessPriority.Normal, width, height, GameWindowSizeMode.Default, "", "PCL-梦港", 4),
            Preference: new DreamPortPortablePreferenceSettings(false, true, LauncherTitleType.Default, "", false, "", "", false, "auto", "auto", "auto", ColorMode.System, darkTheme, ColorTheme.CatBlue, 600, 0, 500, false, false, true, true, true, 13, false));
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode code, object body)
        => new(code)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

    private sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request, cancellationToken));
    }
}
