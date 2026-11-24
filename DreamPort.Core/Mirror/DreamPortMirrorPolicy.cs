using System.Text.RegularExpressions;
using DreamPort.Core.Config;

namespace DreamPort.Core.Mirror;

/// <summary>梦港下载源选择策略。</summary>
public enum DreamPortDownloadSourceMode
{
    /// <summary>自动模式：优先梦港源，失败后降级 BMCLAPI 与官方源。</summary>
    Auto = 0,

    /// <summary>梦港源优先：梦港源，其次官方源，最后 BMCLAPI。</summary>
    DreamPort = 1,

    /// <summary>BMCLAPI 优先：BMCLAPI，其次梦港源与官方源。</summary>
    Bmclapi = 2,

    /// <summary>官方源优先：官方源，其次梦港源与 BMCLAPI。</summary>
    Official = 3
}

/// <summary>下载源类别。</summary>
public enum DreamPortSourceKind
{
    Official,
    Bmclapi,
    DreamPort
}

/// <summary>
/// DreamPort 镜像源地址改写与候选排序（16 条契约规则唯一来源）。
/// 与 DreamPort-Client mirror.ts 与自建 DreamPort-Mirror 部署契约严格对齐。
/// </summary>
public static partial class DreamPortMirrorPolicy
{
    public const string BmclapiHost = "https://bmclapi2.bangbang93.com";

    private sealed record MirrorPathRule(Regex Pattern, Func<string, bool, string> Replacement, bool SelfOnly = false);

    private static readonly MirrorPathRule[] Rules =
    [
        // Mojang：版本清单 / 版本 JSON / assets index / client jar / Java 运行时
        new(LaunchermetaRegex(), (h, _) => h),
        new(LauncherMojangRegex(), (h, _) => h),
        new(PistonMetaRegex(), (h, _) => h),
        new(PistonDataRegex(), (h, _) => h),
        // Mojang：库文件与资源对象
        new(LibrariesMinecraftRegex(), (h, _) => $"{h}/maven"),
        new(ResourcesMinecraftRegex(), (h, _) => $"{h}/assets"),
        // Forge
        new(ForgeFilesRegex(), (h, _) => $"{h}/maven"),
        new(ForgeMavenRegex(), (h, _) => $"{h}/maven"),
        // NeoForge：官方 /releases 折进镜像 /maven
        new(NeoForgeReleasesRegex(), (h, _) => $"{h}/maven"),
        // Fabric
        new(FabricMetaRegex(), (h, _) => $"{h}/fabric-meta"),
        new(FabricMavenRegex(), (h, isBmclapi) => $"{h}/{(isBmclapi ? "maven" : "fabric-maven")}"),
        // Quilt
        new(QuiltMetaRegex(), (h, _) => $"{h}/quilt-meta", SelfOnly: true),
        new(QuiltMavenRegex(), (h, _) => $"{h}/maven"),
        // Legacy Fabric
        new(LegacyFabricMetaRegex(), (h, _) => $"{h}/legacyfabric-meta", SelfOnly: true),
        new(LegacyFabricMavenRegex(), (h, _) => $"{h}/legacyfabric-maven", SelfOnly: true),
        // LiteLoader
        new(LiteLoaderRegex(), (h, _) => $"{h}/liteloader", SelfOnly: true),
        // Cleanroom
        new(CleanroomMavenRegex(), (h, _) => $"{h}/cleanroom-maven", SelfOnly: true),
        // SpongePowered maven
        new(SpongeMavenRegex(), (h, _) => $"{h}/maven"),
        // LabyMod（区分 S3 与 R2 API / libraries，避免误拼 /maven）
        new(LabyModS3Regex(), (h, _) => $"{h}/labymod", SelfOnly: true),
        new(LabyModR2ApiRegex(), (h, _) => $"{h}/labymod-api", SelfOnly: true),
        new(LabyModR2LibsRegex(), (h, _) => $"{h}/labymod-maven", SelfOnly: true)
    ];

    public static bool IsBmclapiHost(string? hostOrUrl)
    {
        if (string.IsNullOrWhiteSpace(hostOrUrl))
            return false;
        var host = hostOrUrl.Contains("://", StringComparison.Ordinal)
            ? GetHostname(hostOrUrl)
            : hostOrUrl.Trim().ToLowerInvariant();
        return host is "bmclapi2.bangbang93.com" or "bmclapi.bangbang93.com";
    }

    /// <summary>将官方 URL 改写为指定镜像主机地址；未匹配任何已知契约规则的第三方 URL 原样返回。</summary>
    public static string RewriteToMirror(string? url, string? mirrorHost = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        var host = string.IsNullOrWhiteSpace(mirrorHost) ? BmclapiHost : mirrorHost.Trim().TrimEnd('/');
        var isBmclapi = IsBmclapiHost(host);
        var current = url.Trim();

        foreach (var rule in Rules)
        {
            if (rule.SelfOnly && isBmclapi)
                continue;
            var replaced = rule.Pattern.Replace(current, rule.Replacement(host, isBmclapi), 1);
            if (!ReferenceEquals(replaced, current) && !string.Equals(replaced, current, StringComparison.Ordinal))
                return replaced;
        }

        return current;
    }

    public static DreamPortSourceKind ClassifyUrl(string? url, string? selfMirrorHost = null)
    {
        var host = GetHostname(url);
        if (string.IsNullOrEmpty(host))
            return DreamPortSourceKind.Official;

        var selfHost = GetHostname(selfMirrorHost ?? DreamPortBuildConfig.MirrorHost);
        if (!string.IsNullOrEmpty(selfHost) && string.Equals(host, selfHost, StringComparison.OrdinalIgnoreCase))
            return DreamPortSourceKind.DreamPort;

        if (IsBmclapiHost(host))
            return DreamPortSourceKind.Bmclapi;

        return DreamPortSourceKind.Official;
    }

    /// <summary>
    /// 按选定模式将官方与镜像候选组合成去重且有序的最终下载 URL 列表。
    /// 若官方地址匹配 16 条镜像规则且启用了自建梦港源，会自动生成对应的梦港源 URL；
    /// 若仅传入了 BMCLAPI 同构 URL 而未匹配官方前缀，同样将其主机替换到自建梦港源作为同构候选。
    /// </summary>
    public static IReadOnlyList<string> BuildCandidateUrls(
        IEnumerable<string>? officialUrls,
        IEnumerable<string>? bmclapiUrls,
        DreamPortDownloadSourceMode mode,
        string? selfMirrorHost = null)
    {
        var officialList = NormalizeDistinct(officialUrls);
        var mirrorList = NormalizeDistinct(bmclapiUrls);
        var effectiveSelfHost = (selfMirrorHost ?? DreamPortBuildConfig.MirrorHost).Trim().TrimEnd('/');
        var hasSelfMirror = Uri.TryCreate(effectiveSelfHost, UriKind.Absolute, out _);

        var dreamPortUrls = new List<string>();
        if (hasSelfMirror)
        {
            foreach (var u in officialList)
            {
                var rewritten = RewriteToMirror(u, effectiveSelfHost);
                if (!string.Equals(rewritten, u, StringComparison.Ordinal))
                    dreamPortUrls.Add(rewritten);
            }

            // 兼容仅传 BMCLAPI 地址（或 BMCLAPI 专用路径如 /forge/minecraft/）的场景
            if (dreamPortUrls.Count == 0)
            {
                foreach (var u in mirrorList)
                {
                    var host = GetHostname(u);
                    if (IsBmclapiHost(host) && Uri.TryCreate(u, UriKind.Absolute, out var parsed))
                    {
                        var converted = effectiveSelfHost + parsed.PathAndQuery;
                        dreamPortUrls.Add(converted);
                    }
                }
            }
        }

        var ordered = mode switch
        {
            DreamPortDownloadSourceMode.Official => Concat(officialList, dreamPortUrls, mirrorList),
            DreamPortDownloadSourceMode.Bmclapi => Concat(mirrorList, dreamPortUrls, officialList),
            DreamPortDownloadSourceMode.DreamPort => Concat(dreamPortUrls, officialList, mirrorList),
            _ => Concat(dreamPortUrls, mirrorList, officialList)
        };

        return NormalizeDistinct(ordered);
    }

    private static IEnumerable<string> Concat(params IEnumerable<string>[] lists)
    {
        foreach (var list in lists)
            foreach (var item in list)
                yield return item;
    }

    private static List<string> NormalizeDistinct(IEnumerable<string>? urls)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (urls is null)
            return result;

        foreach (var raw in urls)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var trimmed = raw.Trim();
            if (seen.Add(trimmed))
                result.Add(trimmed);
        }

        return result;
    }

    private static string GetHostname(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;
        return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            ? uri.Host.ToLowerInvariant()
            : string.Empty;
    }

    [GeneratedRegex(@"^https?://launchermeta\.mojang\.com", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LaunchermetaRegex();

    [GeneratedRegex(@"^https?://launcher\.mojang\.com", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LauncherMojangRegex();

    [GeneratedRegex(@"^https?://piston-meta\.mojang\.com", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PistonMetaRegex();

    [GeneratedRegex(@"^https?://piston-data\.mojang\.com", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PistonDataRegex();

    [GeneratedRegex(@"^https?://libraries\.minecraft\.net", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LibrariesMinecraftRegex();

    [GeneratedRegex(@"^https?://resources\.download\.minecraft\.net", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ResourcesMinecraftRegex();

    [GeneratedRegex(@"^https?://files\.minecraftforge\.net/maven", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ForgeFilesRegex();

    [GeneratedRegex(@"^https?://maven\.minecraftforge\.net", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ForgeMavenRegex();

    [GeneratedRegex(@"^https?://maven\.neoforged\.net/releases", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex NeoForgeReleasesRegex();

    [GeneratedRegex(@"^https?://meta\.fabricmc\.net", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex FabricMetaRegex();

    [GeneratedRegex(@"^https?://maven\.fabricmc\.net", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex FabricMavenRegex();

    [GeneratedRegex(@"^https?://meta\.quiltmc\.org", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex QuiltMetaRegex();

    [GeneratedRegex(@"^https?://maven\.quiltmc\.org/repository/release", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex QuiltMavenRegex();

    [GeneratedRegex(@"^https?://meta\.legacyfabric\.net", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LegacyFabricMetaRegex();

    [GeneratedRegex(@"^https?://maven\.legacyfabric\.net", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LegacyFabricMavenRegex();

    [GeneratedRegex(@"^https?://dl\.liteloader\.com", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LiteLoaderRegex();

    [GeneratedRegex(@"^https?://maven\.cleanroommc\.com", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CleanroomMavenRegex();

    [GeneratedRegex(@"^https?://repo\.spongepowered\.org/maven", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SpongeMavenRegex();

    [GeneratedRegex(@"^https?://laby-releases\.s3\.de\.io\.cloud\.ovh\.net", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LabyModS3Regex();

    [GeneratedRegex(@"^https?://releases\.r2\.labymod\.net/api", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LabyModR2ApiRegex();

    [GeneratedRegex(@"^https?://releases\.r2\.labymod\.net/libraries", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LabyModR2LibsRegex();
}
