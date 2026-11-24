using System.Reflection;
using PCL.Core.App;

namespace DreamPort.Core.Config;

/// <summary>
/// DreamPort 构建期与环境变量配置中心（全项目唯一来源）。
/// 优先读取环境变量覆写，其次回退编译期默认值，禁止在业务代码中硬编码重复地址或 UA。
/// </summary>
public static class DreamPortBuildConfig
{
    public const string DefaultBaseUrl = "https://xmcraft.cn";
    public const string DefaultRepoUrl = "https://github.com/Xiameng/PCL-DreamPort";
    public const string DefaultUpdateMirrorBaseUrl = "https://dl.xmcraft.cn";
    public const string DisplayName = "PCL-梦港";
    public const string RepoName = "PCL-DreamPort";

    /// <summary>DreamPort 站点根地址（不含尾部斜杠）。</summary>
    public static string BaseUrl { get; } = ResolveUrlEnv("DP_BASE_URL", DefaultBaseUrl);

    /// <summary>公开开源仓库地址（关于页与合规声明引用）。</summary>
    public static string RepoUrl { get; } = ResolveUrlEnv("DP_REPO_URL", DefaultRepoUrl);

    /// <summary>启动器更新等配套服务下载加速基址（不用于游戏数据下载）。</summary>
    public static string UpdateMirrorBaseUrl { get; } = ResolveUrlEnv("DP_UPDATE_MIRROR_URL", DefaultUpdateMirrorBaseUrl);

    /// <summary>应用版本号（从元数据或程序集版本单一来源派生）。</summary>
    public static string AppVersion { get; } = ResolveAppVersion();

    /// <summary>常规 API 请求 User-Agent（标识来源，含站点地址；不可直接用于 BMCLAPI CDN 下载层）。</summary>
    public static string UserAgent => $"PCL-DreamPort/{AppVersion} ({DefaultBaseUrl})";

    /// <summary>
    /// 下载层专用 User-Agent（仅「名称/版本」，不含括号与网址）。
    /// BMCLAPI /maven/ 302 跳转 CDN 对含括号或 URL 的 UA 会返回 0 字节空体。
    /// </summary>
    public static string DownloadUserAgent => $"PCL-DreamPort/{AppVersion}";

    private static string ResolveAppVersion()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Basics.VersionName))
                return Basics.VersionName;
        }
        catch
        {
            // 纯单测宿主未嵌入 PCL.metadata.json 时回退到程序集版本
        }

        var ver = typeof(DreamPortBuildConfig).Assembly.GetName().Version;
        return ver is not null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "0.1.0";
    }

    private static string ResolveUrlEnv(string key, string fallback)
    {
        var raw = Environment.GetEnvironmentVariable(key)?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        return Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? raw.TrimEnd('/')
            : fallback;
    }
}
