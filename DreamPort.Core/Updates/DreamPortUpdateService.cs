using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DreamPort.Core.Config;
using PCL.Core.Logging;

namespace DreamPort.Core.Updates;

/// <summary>
/// PCL-DreamPort 双源自动更新引擎（自建 <c>dl.xmcraft.cn</c> 公开镜像清单优先 + GitHub Releases 回落）：
/// - 严格校验平台/架构组合（绝不跨架构误下）；
/// - 镜像清单强制 schema/product/https/sha256 全字段合规才采信，否则回落 GitHub；
/// - GitHub Release 通过同批次 <c>SHA256SUMS</c> 提取校验和；无有效校验和时仅允许跳转发布页手动下载；
/// - 落盘前通过 <c>.dp-update-part</c> 完成 SHA-256 校验，macOS 生成 <c>ditto</c> + <c>open -n</c> 替换脚本。
/// </summary>
public static partial class DreamPortUpdateService
{
    public const string ProductId = "pcl-dreamport";

    private static readonly HashSet<string> AllowedPlatforms = new(StringComparer.Ordinal)
    {
        "mac-arm64",
        "mac-x64",
        "win-x64",
        "win-arm64",
        "linux-x64",
        "linux-arm64"
    };

    public static string? CurrentPlatformTag(OSPlatform? os = null, Architecture? arch = null)
    {
        var effArch = arch ?? RuntimeInformation.ProcessArchitecture;
        if (os is { } explicitOs)
        {
            if (explicitOs == OSPlatform.OSX)
                return ResolveOsxPlatform(effArch);
            if (explicitOs == OSPlatform.Windows)
                return ResolveWindowsPlatform(effArch);
            if (explicitOs == OSPlatform.Linux)
                return ResolveLinuxPlatform(effArch);
            return null;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return ResolveOsxPlatform(effArch);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ResolveWindowsPlatform(effArch);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return ResolveLinuxPlatform(effArch);
        return null;
    }

    private static string? ResolveOsxPlatform(Architecture arch) => arch switch
    {
        Architecture.Arm64 => "mac-arm64",
        Architecture.X64 => "mac-x64",
        _ => null
    };

    private static string? ResolveWindowsPlatform(Architecture arch) => arch switch
    {
        Architecture.X64 => "win-x64",
        Architecture.Arm64 => "win-arm64",
        _ => null
    };

    private static string? ResolveLinuxPlatform(Architecture arch) => arch switch
    {
        Architecture.X64 => "linux-x64",
        Architecture.Arm64 => "linux-arm64",
        _ => null
    };

    public static string BuildMirrorManifestUrl(string mirrorBaseUrl, DreamPortUpdateChannel channel)
    {
        var root = mirrorBaseUrl.Trim().TrimEnd('/');
        var subPath = channel == DreamPortUpdateChannel.Beta
            ? "/pcl-dreamport/beta/latest.json"
            : "/pcl-dreamport/latest.json";
        return root + subPath;
    }

    public static (string Owner, string Repo)? ParseRepoOwnerAndName(string? repoUrl)
    {
        if (!Uri.TryCreate((repoUrl ?? string.Empty).Trim(), UriKind.Absolute, out var uri))
            return null;

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return null;

        var owner = segments[^2].Trim();
        var repo = segments[^1].Trim();
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repo = repo[..^4];

        return string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo) ? null : (owner, repo);
    }

    public static DreamPortUpdateRelease? ParseMirrorManifest(string? jsonText, string product = ProductId)
    {
        if (string.IsNullOrWhiteSpace(jsonText))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (!root.TryGetProperty("schema", out var schemaEl)
                || schemaEl.ValueKind != JsonValueKind.Number
                || !schemaEl.TryGetInt32(out var schema)
                || schema != 1)
            {
                return null;
            }

            if (!root.TryGetProperty("product", out var prodEl)
                || prodEl.ValueKind != JsonValueKind.String
                || !string.Equals(prodEl.GetString(), product, StringComparison.Ordinal))
            {
                return null;
            }

            var version = GetNonEmptyString(root, "version");
            var tag = GetNonEmptyString(root, "tag");
            if (version is null || tag is null)
                return null;

            var notes = string.Empty;
            if (root.TryGetProperty("notes", out var notesEl))
            {
                if (notesEl.ValueKind != JsonValueKind.String && notesEl.ValueKind != JsonValueKind.Null)
                    return null;
                notes = notesEl.GetString() ?? string.Empty;
            }

            string? publishedAt = null;
            if (root.TryGetProperty("publishedAt", out var pubEl) && pubEl.ValueKind != JsonValueKind.Null)
            {
                if (pubEl.ValueKind != JsonValueKind.String)
                    return null;
                publishedAt = pubEl.GetString();
            }

            if (!root.TryGetProperty("assets", out var assetsEl)
                || assetsEl.ValueKind != JsonValueKind.Array
                || assetsEl.GetArrayLength() == 0)
            {
                return null;
            }

            var assets = new List<DreamPortUpdateAsset>();
            foreach (var item in assetsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    return null;

                var name = GetNonEmptyString(item, "name");
                var platform = GetNonEmptyString(item, "platform");
                var sha = GetNonEmptyString(item, "sha256");
                var urlStr = GetNonEmptyString(item, "url");

                if (name is null
                    || platform is null
                    || !AllowedPlatforms.Contains(platform)
                    || sha is null
                    || !Sha256HexPattern().IsMatch(sha)
                    || urlStr is null)
                {
                    return null;
                }

                if (!item.TryGetProperty("size", out var sizeEl)
                    || sizeEl.ValueKind != JsonValueKind.Number
                    || !sizeEl.TryGetInt64(out var size)
                    || size < 0)
                {
                    return null;
                }

                if (!Uri.TryCreate(urlStr, UriKind.Absolute, out var uri)
                    || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                assets.Add(new DreamPortUpdateAsset(
                    Name: name,
                    Url: uri.ToString(),
                    Size: size,
                    Sha256: sha.ToLowerInvariant(),
                    Platform: platform));
            }

            return new DreamPortUpdateRelease(tag, DreamPortSkippedVersionStore.NormalizeTagVersion(version), notes, publishedAt, "mirror", assets);
        }
        catch
        {
            return null;
        }
    }

    public static (DreamPortUpdateRelease? Release, string? ShaSumsUrl) ParseGitHubReleases(
        string? jsonText,
        DreamPortUpdateChannel channel,
        string? targetPlatform)
    {
        if (string.IsNullOrWhiteSpace(jsonText))
            return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return (null, null);

            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (rel.ValueKind != JsonValueKind.Object)
                    continue;

                var draft = rel.TryGetProperty("draft", out var draftEl) && draftEl.ValueKind == JsonValueKind.True;
                if (draft)
                    continue;

                var prerelease = rel.TryGetProperty("prerelease", out var preEl) && preEl.ValueKind == JsonValueKind.True;
                if (channel == DreamPortUpdateChannel.Stable && prerelease)
                    continue;

                var tag = GetNonEmptyString(rel, "tag_name");
                if (tag is null)
                    continue;

                var version = DreamPortSkippedVersionStore.NormalizeTagVersion(tag);
                if (string.IsNullOrEmpty(version))
                    continue;

                var notes = rel.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String
                    ? bodyEl.GetString() ?? string.Empty
                    : string.Empty;
                var publishedAt = GetNonEmptyString(rel, "published_at");

                var assets = new List<DreamPortUpdateAsset>();
                string? shaSumsUrl = null;
                if (rel.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assetsEl.EnumerateArray())
                    {
                        if (a.ValueKind != JsonValueKind.Object)
                            continue;
                        var name = GetNonEmptyString(a, "name");
                        var dlUrl = GetNonEmptyString(a, "browser_download_url");
                        if (name is null || dlUrl is null)
                            continue;

                        if (string.Equals(name, "SHA256SUMS", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                        {
                            shaSumsUrl = dlUrl;
                            continue;
                        }

                        var size = a.TryGetProperty("size", out var szEl) && szEl.TryGetInt64(out var l) ? l : 0L;
                        var matchedPlatform = DetectGitHubAssetPlatform(name);
                        if (targetPlatform is null || string.Equals(matchedPlatform, targetPlatform, StringComparison.Ordinal))
                        {
                            assets.Add(new DreamPortUpdateAsset(name, dlUrl, size, null, matchedPlatform));
                        }
                    }
                }

                return (new DreamPortUpdateRelease(tag, version, notes, publishedAt, "github", assets), shaSumsUrl);
            }

            return (null, null);
        }
        catch
        {
            return (null, null);
        }
    }

    public static string? DetectGitHubAssetPlatform(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var name = fileName.Trim().ToLowerInvariant();
        if (name.EndsWith(".sha256", StringComparison.Ordinal)
            || name.EndsWith(".sig", StringComparison.Ordinal)
            || name.EndsWith(".asc", StringComparison.Ordinal)
            || name.EndsWith(".blockmap", StringComparison.Ordinal)
            || name.EndsWith(".sigstore.json", StringComparison.Ordinal)
            || name.EndsWith(".json", StringComparison.Ordinal)
            || name.Contains("sha256sums", StringComparison.Ordinal))
        {
            return null;
        }

        var isArm64 = Arm64AssetPattern().IsMatch(name);
        var isX64 = X64AssetPattern().IsMatch(name);

        if (MacAssetPattern().IsMatch(name))
        {
            if (isArm64 && !isX64)
                return "mac-arm64";
            if (isX64 && !isArm64)
                return "mac-x64";
            return null;
        }

        if (WinAssetPattern().IsMatch(name))
        {
            if (isArm64 && !isX64)
                return "win-arm64";
            if (isX64 && !isArm64)
                return "win-x64";
            return null;
        }

        if (LinuxAssetPattern().IsMatch(name))
        {
            if (isArm64 && !isX64)
                return "linux-arm64";
            if (isX64 && !isArm64)
                return "linux-x64";
            return null;
        }

        return null;
    }

    public static bool IsValidSha256Hex(string? sha)
        => !string.IsNullOrWhiteSpace(sha) && Sha256HexPattern().IsMatch(sha.Trim());

    public static bool VerifyFileSha256(string filePath, string? expectedSha256, out string? actualSha256)
    {
        actualSha256 = null;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || !IsValidSha256Hex(expectedSha256))
            return false;

        using var sha = SHA256.Create();
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        actualSha256 = Convert.ToHexStringLower(sha.ComputeHash(fs));
        return string.Equals(actualSha256, expectedSha256!.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static DreamPortUpdateAsset? SelectPreferredAsset(
        IReadOnlyList<DreamPortUpdateAsset> assets,
        string? targetPlatform)
    {
        if (assets.Count == 0)
            return null;

        var matched = targetPlatform is null
            ? assets
            : assets.Where(a => string.Equals(a.Platform, targetPlatform, StringComparison.Ordinal)).ToList();
        if (matched.Count == 0)
            return null;

        if (targetPlatform is not null)
        {
            string[] preferredSuffixes = targetPlatform switch
            {
                "mac-arm64" or "mac-x64" => [".zip", ".dmg"],
                "win-x64" or "win-arm64" => [".exe", ".zip"],
                "linux-x64" or "linux-arm64" => [".appimage", ".deb", ".rpm"],
                _ => []
            };

            foreach (var suffix in preferredSuffixes)
            {
                var found = matched.FirstOrDefault(a => a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                if (found is not null)
                    return found;
            }
        }

        return matched[0];
    }

    public static string? ResolveMacAppBundlePathFromBaseDirectory(string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            return null;

        var full = Path.GetFullPath(baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (full.Contains("/AppTranslocation/", StringComparison.OrdinalIgnoreCase))
            return null;

        var dir = new DirectoryInfo(full);
        while (dir is not null)
        {
            if (dir.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }

    public static bool TryResolveCurrentMacAppBundlePath(out string appBundlePath)
    {
        appBundlePath = string.Empty;
        if (!OperatingSystem.IsMacOS())
            return false;

        var resolved = ResolveMacAppBundlePathFromBaseDirectory(AppContext.BaseDirectory);
        if (string.IsNullOrWhiteSpace(resolved) || !Directory.Exists(resolved))
            return false;

        var infoPlist = Path.Combine(resolved, "Contents", "Info.plist");
        if (!File.Exists(infoPlist))
            return false;

        var parentDir = Path.GetDirectoryName(resolved);
        if (string.IsNullOrWhiteSpace(parentDir)
            || resolved.StartsWith("/Volumes/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var probe = Path.Combine(parentDir, $".dp-write-probe-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            appBundlePath = resolved;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string? ParseSha256Sums(string? content, string? assetFileName)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(assetFileName))
            return null;

        var targetName = Path.GetFileName(assetFileName.Trim());
        foreach (var rawLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = ShaSumsLinePattern().Match(rawLine.Trim());
            if (!match.Success)
                continue;

            var lineHash = match.Groups[1].Value.ToLowerInvariant();
            var lineFile = Path.GetFileName(match.Groups[2].Value.Trim().TrimStart('*'));
            if (string.Equals(lineFile, targetName, StringComparison.OrdinalIgnoreCase))
                return lineHash;
        }

        return null;
    }

    public static int CompareVersions(string? leftRaw, string? rightRaw)
    {
        var left = ParseNormalizedSemVer(leftRaw);
        var right = ParseNormalizedSemVer(rightRaw);

        var c = left.Major.CompareTo(right.Major);
        if (c != 0)
            return c;
        c = left.Minor.CompareTo(right.Minor);
        if (c != 0)
            return c;
        c = left.Patch.CompareTo(right.Patch);
        if (c != 0)
            return c;

        return ComparePrerelease(left.Prerelease, right.Prerelease);
    }

    public static async Task<DreamPortUpdateCheckResult> CheckForUpdateAsync(
        DreamPortUpdateChannel channel,
        string? targetPlatform,
        string currentVersion,
        DreamPortSkippedVersionStore? skippedStore = null,
        HttpClient? httpClient = null,
        string? mirrorBaseUrl = null,
        string? repoUrl = null,
        CancellationToken ct = default)
    {
        var ownsHttp = httpClient is null;
        var http = httpClient ?? CreateDefaultHttpClient();
        var cleanCurrent = DreamPortSkippedVersionStore.NormalizeTagVersion(currentVersion);
        if (string.IsNullOrEmpty(cleanCurrent))
            cleanCurrent = "0.0.0";

        try
        {
            DreamPortUpdateRelease? release = null;
            string? shaSumsUrl = null;

            // ① 优先拉取 dl.xmcraft.cn 镜像清单（任一校验不通过即回落 GitHub）
            var effMirror = mirrorBaseUrl ?? DreamPortBuildConfig.UpdateMirrorBaseUrl;
            if (!string.IsNullOrWhiteSpace(effMirror))
            {
                try
                {
                    var manifestUrl = BuildMirrorManifestUrl(effMirror, channel);
                    var raw = await FetchStringOrNullAsync(http, manifestUrl, "application/json", ct).ConfigureAwait(false);
                    var parsed = ParseMirrorManifest(raw);
                    if (parsed is not null
                        && (targetPlatform is null || parsed.Assets.Any(a => string.Equals(a.Platform, targetPlatform, StringComparison.Ordinal))))
                    {
                        release = parsed;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogWrapper.Debug(ex, "DreamPort", "镜像更新清单拉取失败，回落 GitHub Releases");
                }
            }

            // ② 回落 GitHub Releases API
            if (release is null)
            {
                var effRepoUrl = repoUrl ?? DreamPortBuildConfig.RepoUrl;
                var repoInfo = ParseRepoOwnerAndName(effRepoUrl);
                if (repoInfo is { } r)
                {
                    var apiUrl = $"https://api.github.com/repos/{Uri.EscapeDataString(r.Owner)}/{Uri.EscapeDataString(r.Repo)}/releases";
                    var raw = await FetchStringOrNullAsync(http, apiUrl, "application/vnd.github+json", ct).ConfigureAwait(false);
                    (release, shaSumsUrl) = ParseGitHubReleases(raw, channel, targetPlatform);
                }
            }

            if (release is null)
            {
                return new DreamPortUpdateCheckResult(
                    HasUpdate: false,
                    IsSkipped: false,
                    CurrentVersion: cleanCurrent,
                    LatestVersion: cleanCurrent,
                    Notes: null,
                    DownloadUrl: null,
                    Sha256: null,
                    AssetName: null,
                    Source: "github",
                    Message: "当前已是最新版本（暂无远程发布版本）");
            }

            var asset = SelectPreferredAsset(release.Assets, targetPlatform);

            var sha256 = asset?.Sha256;
            if (sha256 is null && asset is not null && !string.IsNullOrEmpty(shaSumsUrl))
            {
                try
                {
                    var sumsText = await FetchStringOrNullAsync(http, shaSumsUrl, "text/plain", ct).ConfigureAwait(false);
                    sha256 = ParseSha256Sums(sumsText, asset.Name);
                }
                catch
                {
                    sha256 = null;
                }
            }

            var hasUpdate = CompareVersions(release.Version, cleanCurrent) > 0;
            var isSkipped = hasUpdate && (skippedStore?.IsVersionSkipped(channel, release.Version) ?? false);
            var msg = !hasUpdate
                ? $"当前已是最新版本（{cleanCurrent}）"
                : asset is null
                    ? $"发现新版本 {release.Version}，当前平台暂无对应安装包，请前往发布页手动下载"
                    : sha256 is null
                        ? $"发现新版本 {release.Version}（缺少 SHA-256 校验和，需前往发布页手动下载）"
                        : $"发现新版本 {release.Version}（来源：{(release.Source == "mirror" ? "梦港加速镜像" : "GitHub Release")}）";

            return new DreamPortUpdateCheckResult(
                HasUpdate: hasUpdate,
                IsSkipped: isSkipped,
                CurrentVersion: cleanCurrent,
                LatestVersion: release.Version,
                Notes: release.Notes,
                DownloadUrl: asset?.Url,
                Sha256: sha256,
                AssetName: asset?.Name,
                Source: release.Source,
                Message: msg);
        }
        finally
        {
            if (ownsHttp)
                http.Dispose();
        }
    }

    public static async Task<(bool Ok, string? Error)> DownloadAndVerifyAsync(
        HttpClient http,
        string url,
        string targetPath,
        string? expectedSha256,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256) || !Sha256HexPattern().IsMatch(expectedSha256.Trim()))
            return (false, "缺少有效的 64 位 SHA-256 校验和，拒绝执行自动下载安装");

        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var partPath = targetPath + ".dp-update-part";
        try
        {
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode)
                    return (false, $"下载失败（HTTP {(int)resp.StatusCode}）");

                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }

            string actualHash;
            using (var sha = SHA256.Create())
            await using (var fs = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                actualHash = Convert.ToHexStringLower(await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false));
            }

            if (!string.Equals(actualHash, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                return (false, $"更新包 SHA-256 校验不一致（期望 {expectedSha256}，实际 {actualHash}）");

            File.Move(partPath, targetPath, overwrite: true);
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            return (false, "已取消更新下载");
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
                // ignore
            }
        }
    }

    /// <summary>
    /// 生成 macOS 无感原子替换并重启脚本（强制使用 <c>ditto</c> 保留签名与扩展属性，并使用 <c>open -n</c> 拉起新进程，禁止 <c>cp -R</c>）。
    /// </summary>
    public static string BuildMacOsDittoReplaceScript(
        int currentPid,
        string stagedAppPath,
        string targetAppPath,
        bool relaunch = true)
    {
        if (currentPid <= 0)
            throw new ArgumentOutOfRangeException(nameof(currentPid));
        if (string.IsNullOrWhiteSpace(stagedAppPath))
            throw new ArgumentException("暂存 .app 路径不能为空。", nameof(stagedAppPath));
        if (string.IsNullOrWhiteSpace(targetAppPath))
            throw new ArgumentException("目标 .app 路径不能为空。", nameof(targetAppPath));

        var qStaged = QuotePosixShell(stagedAppPath);
        var qTarget = QuotePosixShell(targetAppPath);
        var qBackup = QuotePosixShell(targetAppPath + ".dp-update-bak");
        var relaunchLine = relaunch ? $"\nopen -n {qTarget}" : string.Empty;
        return $$"""
            #!/bin/sh
            set -e
            PID={{currentPid}}
            WAIT_COUNT=0
            while kill -0 "$PID" 2>/dev/null; do
              WAIT_COUNT=$((WAIT_COUNT + 1))
              if [ "$WAIT_COUNT" -ge 100 ]; then
                break
              fi
              sleep 0.1
            done
            rm -rf {{qBackup}}
            if [ -e {{qTarget}} ]; then
              mv {{qTarget}} {{qBackup}}
            fi
            if ! ditto {{qStaged}} {{qTarget}}; then
              rm -rf {{qTarget}}
              if [ -e {{qBackup}} ]; then
                mv {{qBackup}} {{qTarget}}
              fi
              exit 1
            fi
            rm -rf {{qBackup}}
            xattr -dr com.apple.quarantine {{qTarget}} 2>/dev/null || true{{relaunchLine}}
            """;
    }

    private static string QuotePosixShell(string path)
        => "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static async Task<string?> FetchStringOrNullAsync(
        HttpClient http,
        string url,
        string acceptHeader,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Accept", acceptHeader);
        req.Headers.TryAddWithoutValidation("User-Agent", DreamPortBuildConfig.UserAgent);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound || !resp.IsSuccessStatusCode)
            return null;
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DreamPortBuildConfig.UserAgent);
        return http;
    }

    private static string? GetNonEmptyString(JsonElement obj, string propName)
    {
        if (!obj.TryGetProperty(propName, out var el) || el.ValueKind != JsonValueKind.String)
            return null;
        var val = el.GetString()?.Trim();
        return string.IsNullOrEmpty(val) ? null : val;
    }

    private static (int Major, int Minor, int Patch, string Prerelease) ParseNormalizedSemVer(string? raw)
    {
        var s = DreamPortSkippedVersionStore.NormalizeTagVersion(raw);
        if (string.IsNullOrEmpty(s))
            return (0, 0, 0, string.Empty);

        var plusIdx = s.IndexOf('+');
        if (plusIdx >= 0)
            s = s[..plusIdx];

        var prerelease = string.Empty;
        var dashIdx = s.IndexOf('-');
        if (dashIdx >= 0)
        {
            prerelease = s[(dashIdx + 1)..].Trim();
            s = s[..dashIdx];
        }

        var parts = s.Split('.');
        var major = parts.Length > 0 && int.TryParse(parts[0], out var m) ? m : 0;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;
        var patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
        return (major, minor, patch, prerelease);
    }

    private static int ComparePrerelease(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
            return 0;
        if (string.IsNullOrEmpty(a))
            return 1;
        if (string.IsNullOrEmpty(b))
            return -1;

        var partsA = a.Split('.');
        var partsB = b.Split('.');
        var len = Math.Min(partsA.Length, partsB.Length);
        for (var i = 0; i < len; i++)
        {
            var aIsNum = int.TryParse(partsA[i], out var numA);
            var bIsNum = int.TryParse(partsB[i], out var numB);
            int cmp;
            if (aIsNum && bIsNum)
                cmp = numA.CompareTo(numB);
            else if (aIsNum || bIsNum)
                cmp = aIsNum ? -1 : 1;
            else
                cmp = string.Compare(partsA[i], partsB[i], StringComparison.OrdinalIgnoreCase);

            if (cmp != 0)
                return cmp;
        }

        return partsA.Length.CompareTo(partsB.Length);
    }

    [GeneratedRegex(@"^[0-9a-fA-F]{64}$", RegexOptions.Compiled)]
    private static partial Regex Sha256HexPattern();

    [GeneratedRegex(@"^([0-9a-fA-F]{64})\s+\*?(.+)$", RegexOptions.Compiled)]
    private static partial Regex ShaSumsLinePattern();

    [GeneratedRegex(@"(?:^|[-_.])(mac|macos|osx|darwin)(?:[-_.]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MacAssetPattern();

    [GeneratedRegex(@"(?:^|[-_.])(win|windows)(?:[-_.]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex WinAssetPattern();

    [GeneratedRegex(@"(?:^|[-_.])(linux|appimage)(?:[-_.]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LinuxAssetPattern();

    [GeneratedRegex(@"(?:^|[-_.])(arm64|aarch64)(?:[-_.]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex Arm64AssetPattern();

    [GeneratedRegex(@"(?:^|[-_.])(x64|x86_64|amd64)(?:[-_.]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex X64AssetPattern();
}
