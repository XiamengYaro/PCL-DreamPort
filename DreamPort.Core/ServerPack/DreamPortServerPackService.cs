using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DreamPort.Core.Api;
using DreamPort.Core.Config;

namespace DreamPort.Core.ServerPack;

/// <summary>
/// DreamPort 服务器配套模组双源同步核心服务（自研直传清单 + Modrinth 指定模组）：
/// - 同步前强制校验实例 MC 版本与加载器；
/// - 任意越界或核心保护路径整单拒绝；
/// - 仅管理 <c>.dp-serverpack.json</c> 受管集合，保留玩家自装模组与 <c>.disabled</c> 禁用状态；
/// - 损坏状态文件备份并拒绝按空基线同步。
/// </summary>
public static class DreamPortServerPackService
{
    public const string StateFileName = ".dp-serverpack.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static bool IsSafePackPath(string? relativePath, string instanceId = "")
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        if (relativePath.Contains('\\') || relativePath.StartsWith('/'))
            return false;

        foreach (var ch in relativePath)
        {
            if (char.IsControl(ch))
                return false;
        }

        if (relativePath.Length >= 2 && char.IsLetter(relativePath[0]) && relativePath[1] == ':')
            return false;

        var parts = relativePath.Split('/');
        if (parts.Length == 0)
            return false;

        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part) || part is "." or "..")
                return false;
        }

        var first = parts[0];
        if (string.Equals(first, "versions", StringComparison.OrdinalIgnoreCase)
            || first.StartsWith(".dp-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var cleanId = (instanceId ?? string.Empty).Trim();
        if (parts.Length == 1 && !string.IsNullOrEmpty(cleanId))
        {
            if (string.Equals(first, $"{cleanId}.json", StringComparison.OrdinalIgnoreCase)
                || string.Equals(first, $"{cleanId}.jar", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    public static DreamPortServerPackCompatibilityResult CheckCompatibility(
        string? requiredGameVersion,
        IReadOnlyList<string>? requiredLoaders,
        string? instanceGameVersion,
        string? instanceLoader)
    {
        var reqGame = (requiredGameVersion ?? string.Empty).Trim();
        var reqLoaders = (requiredLoaders ?? [])
            .Select(x => (x ?? string.Empty).Trim().ToLowerInvariant())
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var instGame = (instanceGameVersion ?? string.Empty).Trim();
        var instLoader = NormalizeLoader(instanceLoader);

        if (!string.IsNullOrEmpty(reqGame)
            && !string.Equals(instGame, reqGame, StringComparison.OrdinalIgnoreCase))
        {
            var reason = string.IsNullOrEmpty(instGame)
                ? $"无法识别当前实例的 Minecraft 版本，服务器配套模组要求 {reqGame}"
                : $"当前实例为 Minecraft {instGame}，服务器配套模组要求 {reqGame}";
            return new DreamPortServerPackCompatibilityResult(false, reason);
        }

        if (reqLoaders.Length > 0 && !reqLoaders.Contains(instLoader, StringComparer.OrdinalIgnoreCase))
        {
            var expected = string.Join(" / ", reqLoaders);
            var actual = string.IsNullOrEmpty(instLoader) || instLoader == "vanilla"
                ? "原版（无模组加载器）"
                : instLoader;
            return new DreamPortServerPackCompatibilityResult(
                false,
                $"当前实例加载器为 {actual}，服务器配套模组要求 {expected}");
        }

        return new DreamPortServerPackCompatibilityResult(true);
    }

    public static async Task<DreamPortServerPackPlan> PlanSyncAsync(
        DreamPortServerPackManifestResult manifest,
        string instanceDir,
        string instanceId = "",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instanceDir))
            throw new ArgumentException("实例目录不能为空。", nameof(instanceDir));

        var effectiveId = string.IsNullOrWhiteSpace(instanceId)
            ? Path.GetFileName(instanceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : instanceId.Trim();

        var state = ReadStateOrThrow(instanceDir);
        var downloads = new List<DreamPortServerPackDownloadItem>();
        var deletes = new List<string>();
        var skippedDisabled = new List<string>();
        var rejected = new List<string>();
        var wanted = new Dictionary<string, DreamPortServerPackFile>(StringComparer.Ordinal);
        var upToDate = 0;

        foreach (var file in manifest.Files)
        {
            if (!IsSafePackPath(file.Path, effectiveId))
            {
                rejected.Add(file.Path ?? string.Empty);
                continue;
            }

            wanted[file.Path!] = file;
        }

        if (rejected.Count > 0)
            return new DreamPortServerPackPlan([], [], [], 0, rejected);

        foreach (var file in wanted.Values)
        {
            ct.ThrowIfCancellationRequested();

            if (IsDisabled(instanceDir, file.Path))
            {
                skippedDisabled.Add(file.Path);
                continue;
            }

            var abs = ResolveSafeTargetPath(instanceDir, file.Path);
            if (!File.Exists(abs))
            {
                downloads.Add(new DreamPortServerPackDownloadItem(file.Path, file.Sha256, file.Size, "missing"));
                continue;
            }

            var localHash = await ComputeSha256HexAsync(abs, ct).ConfigureAwait(false);
            if (!string.Equals(localHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                downloads.Add(new DreamPortServerPackDownloadItem(file.Path, file.Sha256, file.Size, "mismatch"));
                continue;
            }

            upToDate++;
        }

        if (state is not null)
        {
            foreach (var oldRel in state.Managed.Keys)
            {
                if (wanted.ContainsKey(oldRel))
                    continue;
                if (!IsSafePackPath(oldRel, effectiveId))
                    continue;
                if (IsDisabled(instanceDir, oldRel))
                    continue;
                deletes.Add(oldRel);
            }
        }

        return new DreamPortServerPackPlan(downloads, deletes, skippedDisabled, upToDate, []);
    }

    public static async Task<IReadOnlyList<DreamPortModrinthPlanItem>> InspectModrinthModsAsync(
        IReadOnlyList<DreamPortModrinthModSpec> mods,
        string instanceDir,
        string gameVersion,
        string loader,
        HttpClient httpClient,
        CancellationToken ct = default)
    {
        if (mods.Count == 0)
            return [];

        var modsDir = Path.Combine(instanceDir, "mods");
        var cleanVer = (gameVersion ?? string.Empty).Trim();
        var cleanLoader = NormalizeLoader(loader);
        var items = new List<DreamPortModrinthPlanItem>(mods.Count);

        foreach (var mod in mods)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var resolved = await ResolveModrinthPrimaryFileAsync(mod, cleanVer, cleanLoader, httpClient, ct)
                    .ConfigureAwait(false);
                if (resolved is null)
                {
                    items.Add(new DreamPortModrinthPlanItem(
                        mod.ProjectId, mod.Slug, mod.Title, mod.VersionId, mod.Required,
                        FileName: null, DownloadUrl: null, Sha512: null, Sha1: null, Size: 0,
                        AlreadyInstalled: false, Disabled: false,
                        ResolveError: "未在 Modrinth 找到匹配当前 MC 版本与加载器的版本文件"));
                    continue;
                }

                var targetPath = Path.Combine(modsDir, resolved.FileName);
                var disabled = File.Exists(targetPath + ".disabled");
                var alreadyInstalled = false;
                if (!disabled && File.Exists(targetPath))
                {
                    if (!string.IsNullOrEmpty(resolved.Sha512))
                    {
                        var actual = await ComputeSha512HexAsync(targetPath, ct).ConfigureAwait(false);
                        alreadyInstalled = string.Equals(actual, resolved.Sha512, StringComparison.OrdinalIgnoreCase);
                    }
                    else if (!string.IsNullOrEmpty(resolved.Sha1))
                    {
                        var actual = await ComputeSha1HexAsync(targetPath, ct).ConfigureAwait(false);
                        alreadyInstalled = string.Equals(actual, resolved.Sha1, StringComparison.OrdinalIgnoreCase);
                    }
                }

                items.Add(new DreamPortModrinthPlanItem(
                    mod.ProjectId,
                    mod.Slug,
                    mod.Title,
                    mod.VersionId,
                    mod.Required,
                    resolved.FileName,
                    resolved.Url,
                    resolved.Sha512,
                    resolved.Sha1,
                    resolved.Size,
                    alreadyInstalled,
                    disabled));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                items.Add(new DreamPortModrinthPlanItem(
                    mod.ProjectId, mod.Slug, mod.Title, mod.VersionId, mod.Required,
                    FileName: null, DownloadUrl: null, Sha512: null, Sha1: null, Size: 0,
                    AlreadyInstalled: false, Disabled: false,
                    ResolveError: ex.Message));
            }
        }

        return items;
    }

    public static async Task<DreamPortServerPackApplyResult> ApplySyncAsync(
        DreamPortApiClient api,
        DreamPortServerPackManifestResult manifest,
        string instanceDir,
        string instanceGameVersion,
        string instanceLoader,
        string instanceId = "",
        HttpClient? modrinthHttpClient = null,
        IProgress<DreamPortServerPackProgress>? progress = null,
        CancellationToken ct = default)
    {
        var compat = CheckCompatibility(manifest.GameVersion, manifest.Loaders, instanceGameVersion, instanceLoader);
        if (!compat.Compatible)
        {
            return new DreamPortServerPackApplyResult(
                Ok: false,
                Cancelled: false,
                Message: compat.Reason ?? "当前实例不兼容服务器配套模组",
                DownloadedCount: 0,
                DeletedCount: 0,
                ModrinthInstalledCount: 0,
                Failed: [],
                Rejected: []);
        }

        var effectiveId = string.IsNullOrWhiteSpace(instanceId)
            ? Path.GetFileName(instanceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : instanceId.Trim();

        DreamPortServerPackPlan plan;
        ServerPackState? prevState;
        try
        {
            prevState = ReadStateOrThrow(instanceDir);
            plan = await PlanSyncAsync(manifest, instanceDir, effectiveId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new DreamPortServerPackApplyResult(
                Ok: false,
                Cancelled: false,
                Message: ex.Message,
                DownloadedCount: 0,
                DeletedCount: 0,
                ModrinthInstalledCount: 0,
                Failed: [ex.Message],
                Rejected: []);
        }

        if (plan.Rejected.Count > 0)
        {
            return new DreamPortServerPackApplyResult(
                Ok: false,
                Cancelled: false,
                Message: $"服务端清单包含 {plan.Rejected.Count} 条不安全路径，已拒绝同步",
                DownloadedCount: 0,
                DeletedCount: 0,
                ModrinthInstalledCount: 0,
                Failed: [],
                Rejected: plan.Rejected);
        }

        IReadOnlyList<DreamPortModrinthPlanItem> modrinthPlan = [];
        var ownsModrinthHttp = modrinthHttpClient is null;
        var mrHttp = modrinthHttpClient ?? CreateDefaultModrinthHttpClient();
        try
        {
            if (manifest.ModrinthMods.Count > 0)
            {
                progress?.Report(new DreamPortServerPackProgress(0, Math.Max(1, plan.Downloads.Count), "正在解析 Modrinth 指定模组..."));
                modrinthPlan = await InspectModrinthModsAsync(
                    manifest.ModrinthMods,
                    instanceDir,
                    instanceGameVersion,
                    instanceLoader,
                    mrHttp,
                    ct).ConfigureAwait(false);
            }

            var mrPending = modrinthPlan.Where(x => !x.AlreadyInstalled && !x.Disabled).ToList();
            var totalSteps = plan.Downloads.Count + plan.Deletes.Count + mrPending.Count;
            var done = 0;
            var downloadedCount = 0;
            var deletedCount = 0;
            var modrinthInstalledCount = 0;
            var failed = new List<string>();
            var managed = new Dictionary<string, string>(prevState?.Managed ?? new Dictionary<string, string>(StringComparer.Ordinal), StringComparer.Ordinal);

            // 1. 下载服务器直传文件
            foreach (var item in plan.Downloads)
            {
                if (ct.IsCancellationRequested)
                {
                    return new DreamPortServerPackApplyResult(
                        Ok: false,
                        Cancelled: true,
                        Message: "已取消配套模组同步（未执行删除）",
                        DownloadedCount: downloadedCount,
                        DeletedCount: 0,
                        ModrinthInstalledCount: 0,
                        Failed: failed,
                        Rejected: []);
                }

                if (!IsSafePackPath(item.Path, effectiveId))
                {
                    failed.Add($"{item.Path}（路径越界已拒绝）");
                    done++;
                    continue;
                }

                progress?.Report(new DreamPortServerPackProgress(done, Math.Max(1, totalSteps), $"正在下载 {item.Path}...", item.Path));
                var absTarget = ResolveSafeTargetPath(instanceDir, item.Path);
                var ok = await DownloadAndVerifyFileAsync(
                    mrHttp,
                    api.BuildServerPackFileUrl(item.Path),
                    absTarget,
                    expectedSha256: item.Sha256,
                    expectedSha512: null,
                    expectedSha1: null,
                    ct).ConfigureAwait(false);

                if (ct.IsCancellationRequested && !ok.Ok)
                {
                    return new DreamPortServerPackApplyResult(
                        Ok: false,
                        Cancelled: true,
                        Message: "已取消配套模组同步（未执行删除）",
                        DownloadedCount: downloadedCount,
                        DeletedCount: 0,
                        ModrinthInstalledCount: 0,
                        Failed: failed,
                        Rejected: []);
                }

                if (ok.Ok)
                {
                    managed[item.Path] = item.Sha256;
                    downloadedCount++;
                }
                else
                {
                    failed.Add($"{item.Path}（{ok.Error ?? "下载或校验失败"}）");
                }

                done++;
            }

            if (ct.IsCancellationRequested)
            {
                return new DreamPortServerPackApplyResult(
                    Ok: false,
                    Cancelled: true,
                    Message: "已取消配套模组同步（未执行删除）",
                    DownloadedCount: downloadedCount,
                    DeletedCount: 0,
                    ModrinthInstalledCount: 0,
                    Failed: failed,
                    Rejected: []);
            }

            // 2. 删除已从清单移除的旧受管文件（仅删自己管理过且未被禁用的）
            foreach (var rel in plan.Deletes)
            {
                if (!IsSafePackPath(rel, effectiveId))
                {
                    done++;
                    continue;
                }

                progress?.Report(new DreamPortServerPackProgress(done, Math.Max(1, totalSteps), $"正在清理已移除文件 {rel}...", rel));
                try
                {
                    var absTarget = ResolveSafeTargetPath(instanceDir, rel);
                    if (File.Exists(absTarget))
                        File.Delete(absTarget);
                    if (File.Exists(absTarget + ".disabled"))
                        File.Delete(absTarget + ".disabled");
                    managed.Remove(rel);
                    deletedCount++;
                }
                catch (Exception ex)
                {
                    failed.Add($"{rel}（移除失败：{ex.Message}）");
                }

                done++;
            }

            // 3. 下载并校验 Modrinth 指定模组
            if (mrPending.Count > 0)
            {
                var modsDir = Path.Combine(instanceDir, "mods");
                Directory.CreateDirectory(modsDir);
                foreach (var mr in mrPending)
                {
                    if (ct.IsCancellationRequested)
                    {
                        return new DreamPortServerPackApplyResult(
                            Ok: false,
                            Cancelled: true,
                            Message: "已取消配套模组同步",
                            DownloadedCount: downloadedCount,
                            DeletedCount: deletedCount,
                            ModrinthInstalledCount: modrinthInstalledCount,
                            Failed: failed,
                            Rejected: []);
                    }

                    if (!string.IsNullOrWhiteSpace(mr.ResolveError)
                        || string.IsNullOrWhiteSpace(mr.FileName)
                        || string.IsNullOrWhiteSpace(mr.DownloadUrl)
                        || !IsSafePackPath($"mods/{mr.FileName}", effectiveId))
                    {
                        if (mr.Required)
                            failed.Add($"{mr.Title}（{mr.ResolveError ?? "无效的 Modrinth 文件"}）");
                        done++;
                        continue;
                    }

                    progress?.Report(new DreamPortServerPackProgress(done, Math.Max(1, totalSteps), $"正在安装 Modrinth 模组 {mr.Title}...", $"mods/{mr.FileName}"));
                    var target = Path.Combine(modsDir, mr.FileName);
                    var res = await DownloadAndVerifyFileAsync(
                        mrHttp,
                        mr.DownloadUrl,
                        target,
                        expectedSha256: null,
                        expectedSha512: mr.Sha512,
                        expectedSha1: mr.Sha1,
                        ct).ConfigureAwait(false);

                    if (res.Ok)
                        modrinthInstalledCount++;
                    else if (mr.Required)
                        failed.Add($"{mr.Title}（{res.Error ?? "下载或校验失败"}）");

                    done++;
                }
            }

            // 4. 原子持久化受管集合（仅记录成功落盘的文件）
            WriteStateAtomic(instanceDir, new ServerPackState
            {
                Version = manifest.Version,
                Managed = managed,
                SyncedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

            progress?.Report(new DreamPortServerPackProgress(Math.Max(1, totalSteps), Math.Max(1, totalSteps), "配套模组同步结束"));

            var allOk = failed.Count == 0;
            var summary = allOk
                ? $"配套模组同步完成（下载 {downloadedCount} 个，清理 {deletedCount} 个，Modrinth {modrinthInstalledCount} 个）"
                : $"配套模组同步完成，但有 {failed.Count} 项失败";
            return new DreamPortServerPackApplyResult(
                Ok: allOk,
                Cancelled: false,
                Message: summary,
                DownloadedCount: downloadedCount,
                DeletedCount: deletedCount,
                ModrinthInstalledCount: modrinthInstalledCount,
                Failed: failed,
                Rejected: []);
        }
        catch (OperationCanceledException)
        {
            return new DreamPortServerPackApplyResult(
                Ok: false,
                Cancelled: true,
                Message: "已取消配套模组同步",
                DownloadedCount: 0,
                DeletedCount: 0,
                ModrinthInstalledCount: 0,
                Failed: [],
                Rejected: []);
        }
        finally
        {
            if (ownsModrinthHttp)
                mrHttp.Dispose();
        }
    }

    private static HttpClient CreateDefaultModrinthHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DreamPortBuildConfig.UserAgent);
        return http;
    }

    private static async Task<ResolvedModrinthFile?> ResolveModrinthPrimaryFileAsync(
        DreamPortModrinthModSpec mod,
        string gameVersion,
        string loader,
        HttpClient http,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(mod.VersionId))
        {
            var url = $"https://api.modrinth.com/v2/version/{Uri.EscapeDataString(mod.VersionId)}";
            using var doc = await GetJsonAsync(http, url, ct).ConfigureAwait(false);
            return doc is null ? null : ExtractPrimaryFile(doc.RootElement);
        }

        var idOrSlug = !string.IsNullOrWhiteSpace(mod.ProjectId) ? mod.ProjectId : mod.Slug;
        if (string.IsNullOrWhiteSpace(idOrSlug))
            return null;

        var qs = new List<string>();
        if (!string.IsNullOrEmpty(gameVersion))
            qs.Add("game_versions=" + Uri.EscapeDataString($"[\"{gameVersion}\"]"));
        if (!string.IsNullOrEmpty(loader) && loader != "vanilla")
            qs.Add("loaders=" + Uri.EscapeDataString($"[\"{loader}\"]"));

        var reqUrl = $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(idOrSlug)}/version"
            + (qs.Count > 0 ? "?" + string.Join("&", qs) : string.Empty);
        using var listDoc = await GetJsonAsync(http, reqUrl, ct).ConfigureAwait(false);
        if (listDoc is null || listDoc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var ver in listDoc.RootElement.EnumerateArray())
        {
            var file = ExtractPrimaryFile(ver);
            if (file is not null)
                return file;
        }

        return null;
    }

    private static async Task<JsonDocument?> GetJsonAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound || !resp.IsSuccessStatusCode)
            return null;
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
    }

    private static ResolvedModrinthFile? ExtractPrimaryFile(JsonElement versionObj)
    {
        if (versionObj.ValueKind != JsonValueKind.Object
            || !versionObj.TryGetProperty("files", out var filesEl)
            || filesEl.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement? chosen = null;
        foreach (var f in filesEl.EnumerateArray())
        {
            if (f.ValueKind != JsonValueKind.Object)
                continue;
            var primary = f.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True;
            if (primary)
            {
                chosen = f;
                break;
            }
            chosen ??= f;
        }

        if (chosen is not { } el)
            return null;

        var fileName = el.TryGetProperty("filename", out var fnEl) ? fnEl.GetString() : null;
        var dlUrl = el.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(dlUrl))
            return null;

        string? sha512 = null;
        string? sha1 = null;
        if (el.TryGetProperty("hashes", out var hashEl) && hashEl.ValueKind == JsonValueKind.Object)
        {
            sha512 = hashEl.TryGetProperty("sha512", out var s512) ? s512.GetString() : null;
            sha1 = hashEl.TryGetProperty("sha1", out var s1) ? s1.GetString() : null;
        }

        var size = el.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var l) ? l : 0L;
        return new ResolvedModrinthFile(fileName, dlUrl, sha512, sha1, size);
    }

    private static async Task<(bool Ok, string? Error)> DownloadAndVerifyFileAsync(
        HttpClient http,
        string url,
        string absTarget,
        string? expectedSha256,
        string? expectedSha512,
        string? expectedSha1,
        CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(absTarget);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var partPath = absTarget + ".dp-part";
        try
        {
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode)
                    return (false, $"HTTP {(int)resp.StatusCode}");

                await using var netStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await netStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                var actual = await ComputeSha256HexAsync(partPath, ct).ConfigureAwait(false);
                if (!string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    return (false, "SHA-256 校验不一致");
            }
            else if (!string.IsNullOrWhiteSpace(expectedSha512))
            {
                var actual = await ComputeSha512HexAsync(partPath, ct).ConfigureAwait(false);
                if (!string.Equals(actual, expectedSha512.Trim(), StringComparison.OrdinalIgnoreCase))
                    return (false, "SHA-512 校验不一致");
            }
            else if (!string.IsNullOrWhiteSpace(expectedSha1))
            {
                var actual = await ComputeSha1HexAsync(partPath, ct).ConfigureAwait(false);
                if (!string.Equals(actual, expectedSha1.Trim(), StringComparison.OrdinalIgnoreCase))
                    return (false, "SHA-1 校验不一致");
            }

            File.Move(partPath, absTarget, overwrite: true);
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            return (false, "已取消");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(partPath))
                    File.Delete(partPath);
            }
            catch
            {
                // ignore cleanup failure
            }
        }
    }

    private static ServerPackState? ReadStateOrThrow(string instanceDir)
    {
        var path = Path.Combine(instanceDir, StateFileName);
        if (!File.Exists(path))
            return null;

        try
        {
            var raw = File.ReadAllText(path, Encoding.UTF8);
            var state = JsonSerializer.Deserialize<ServerPackState>(raw, JsonOptions);
            if (state is null || state.Managed is null)
                throw new InvalidDataException("同步状态缺少 managed 字段");
            return state;
        }
        catch (Exception ex)
        {
            var backupName = $"{StateFileName}.corrupt.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            var backupPath = Path.Combine(instanceDir, backupName);
            try { File.Copy(path, backupPath, overwrite: true); } catch { /* ignore */ }
            throw new InvalidDataException($"服务端包同步状态文件损坏（已备份为 {backupName}），已拒绝按空基线同步：{ex.Message}", ex);
        }
    }

    private static void WriteStateAtomic(string instanceDir, ServerPackState state)
    {
        var target = Path.Combine(instanceDir, StateFileName);
        var tmp = target + ".tmp";
        var json = JsonSerializer.Serialize(state, JsonOptions);
        File.WriteAllText(tmp, json, Encoding.UTF8);
        File.Move(tmp, target, overwrite: true);
    }

    private static bool IsDisabled(string instanceDir, string relPath)
    {
        try
        {
            return File.Exists(ResolveSafeTargetPath(instanceDir, relPath) + ".disabled");
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveSafeTargetPath(string instanceDir, string relPath)
    {
        var root = Path.GetFullPath(instanceDir);
        var target = Path.GetFullPath(Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!target.StartsWith(rootWithSep, StringComparison.Ordinal))
            throw new InvalidOperationException($"检测到越界路径：{relPath}");
        return target;
    }

    private static async Task<string> ComputeSha256HexAsync(string path, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task<string> ComputeSha512HexAsync(string path, CancellationToken ct)
    {
        using var sha = SHA512.Create();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task<string> ComputeSha1HexAsync(string path, CancellationToken ct)
    {
        using var sha = SHA1.Create();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static string NormalizeLoader(string? loader)
    {
        var raw = (loader ?? string.Empty).Trim().ToLowerInvariant();
        return raw is "原版" or "" ? "vanilla" : raw;
    }

    private sealed class ServerPackState
    {
        public string Version { get; set; } = string.Empty;
        public Dictionary<string, string> Managed { get; set; } = new(StringComparer.Ordinal);
        public long SyncedAtMs { get; set; }
    }

    private sealed record ResolvedModrinthFile(
        string FileName,
        string Url,
        string? Sha512,
        string? Sha1,
        long Size);
}
