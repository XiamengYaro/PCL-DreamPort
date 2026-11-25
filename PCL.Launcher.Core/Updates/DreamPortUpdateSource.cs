using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DreamPort.Core.Config;
using DreamPort.Core.Updates;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.IO;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

/// <summary>
/// DreamPort 双源自动更新器适配器（自建 <c>dl.xmcraft.cn</c> 公开镜像清单优先，回落 GitHub Releases + <c>SHA256SUMS</c>）。
/// </summary>
public sealed class DreamPortUpdateSource : IUpdateSource
{
    private readonly ConcurrentDictionary<(UpdateSourceChannel Channel, UpdateArch Arch), DreamPortUpdateCheckResult> _cache = new();

    public string SourceName { get; set; } = "DreamPort";

    public bool IsAvailable()
        => !string.IsNullOrWhiteSpace(DreamPortBuildConfig.RepoUrl)
           || !string.IsNullOrWhiteSpace(DreamPortBuildConfig.UpdateMirrorBaseUrl);

    public bool RefreshCache()
    {
        var channel = Config.Update.UpdateChannel == UpdateChannel.Beta
            ? DreamPortUpdateChannel.Beta
            : DreamPortUpdateChannel.Stable;
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? UpdateArch.Arm64
            : UpdateArch.X64;
        var pclChannel = channel == DreamPortUpdateChannel.Beta
            ? UpdateSourceChannel.Beta
            : UpdateSourceChannel.Stable;

        var res = FetchCheckResult(pclChannel, arch);
        return res.HasUpdate || res.LatestVersion is not null;
    }

    public VersionDataModel GetLatestVersion(UpdateSourceChannel channel, UpdateArch arch)
    {
        var check = _cache.TryGetValue((channel, arch), out var existing)
            ? existing
            : FetchCheckResult(channel, arch);

        var versionName = !string.IsNullOrWhiteSpace(check.LatestVersion)
            ? NormalizeForPclSemVer(check.LatestVersion)
            : LauncherCommon.VersionBaseName;

        return new VersionDataModel
        {
            VersionName = versionName,
            VersionCode = LauncherCommon.VersionCode,
            Sha256 = check.Sha256 ?? string.Empty,
            Source = check.Source == "mirror" ? $"{SourceName} 加速镜像" : "GitHub Release",
            Changelog = check.Notes ?? string.Empty
        };
    }

    public bool IsLatest(
        UpdateSourceChannel channel,
        UpdateArch arch,
        SemVer currentVersion,
        int currentVersionCode)
    {
        var check = FetchCheckResult(channel, arch);
        if (!check.HasUpdate || string.IsNullOrWhiteSpace(check.LatestVersion))
            return true;

        return DreamPortUpdateService.CompareVersions(check.LatestVersion, currentVersion.ToString()) <= 0;
    }

    public VersionAnnouncementDataModel GetAnnouncementList()
        // 站点公告由 DreamPort /api/announcements 独立承载，此处返回空列表避免回流上游 MinIO 公告
        => new() { Content = [] };

    public List<Loading.LoaderBase> GetDownloadLoader(
        UpdateSourceChannel channel,
        UpdateArch arch,
        string output)
    {
        var check = _cache.TryGetValue((channel, arch), out var existing)
            ? existing
            : FetchCheckResult(channel, arch);

        if (string.IsNullOrWhiteSpace(check.DownloadUrl) || string.IsNullOrWhiteSpace(check.Sha256))
            throw new InvalidOperationException(check.Message);

        var tempDir = Path.Combine(LauncherPaths.Temp, "Cache", "Update", "Download");
        Directory.CreateDirectory(tempDir);
        var tempFileName = string.IsNullOrWhiteSpace(check.AssetName) ? $"{check.Sha256}.bin" : check.AssetName;
        var tempPath = Path.Combine(tempDir, tempFileName);

        var loaders = new List<Loading.LoaderBase>
        {
            new Loading.LoaderTask<int, List<DownloadFile>>(
                Lang.Text("Update.Task.GetVersionInfo"),
                load =>
                {
                    load.output = [new DownloadFile([check.DownloadUrl], tempPath)];
                }),
            new LoaderDownload(Lang.Text("Update.Task.DownloadFile"), [])
            {
                DisplayNameFactory = () => Lang.Text("Update.Task.DownloadFile")
            },
            new Loading.LoaderTask<string, int>(
                Lang.Text("Update.Task.ApplyFile"),
                _ =>
                {
                    var outDir = Path.GetDirectoryName(output);
                    if (!string.IsNullOrEmpty(outDir))
                        Directory.CreateDirectory(outDir);
                    File.Copy(tempPath, output, overwrite: true);
                })
            {
                DisplayNameFactory = () => Lang.Text("Update.Task.ApplyFile")
            }
        };

        return loaders;
    }

    private DreamPortUpdateCheckResult FetchCheckResult(UpdateSourceChannel channel, UpdateArch arch)
    {
        var dpChannel = channel == UpdateSourceChannel.Beta
            ? DreamPortUpdateChannel.Beta
            : DreamPortUpdateChannel.Stable;
        var targetArch = arch == UpdateArch.Arm64 ? Architecture.Arm64 : Architecture.X64;
        var targetOs = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? OSPlatform.OSX
            : RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? OSPlatform.Windows
                : OSPlatform.Linux;
        var platformTag = DreamPortUpdateService.CurrentPlatformTag(targetOs, targetArch);

        var res = DreamPortUpdateService.CheckForUpdateAsync(
                dpChannel,
                platformTag,
                LauncherCommon.VersionBaseName)
            .GetAwaiter()
            .GetResult();
        _cache[(channel, arch)] = res;
        return res;
    }

    private static string NormalizeForPclSemVer(string raw)
    {
        var clean = DreamPortSkippedVersionStore.NormalizeTagVersion(raw);
        if (SemVer.TryParse(clean, out _))
            return clean;
        return LauncherCommon.VersionBaseName;
    }
}
