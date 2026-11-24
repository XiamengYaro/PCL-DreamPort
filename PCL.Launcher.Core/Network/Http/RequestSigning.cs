using System.Net;
using DreamPort.Core.Config;
using PCL.Core.App;

namespace PCL.Network;

public static class RequestSigning
{
    internal static string SecretCdnSign(string urlWithMark)
    {
        if (!urlWithMark.EndsWithF("{CDN}"))
            return urlWithMark;
        return urlWithMark.Replace("{CDN}", "").Replace(" ", "%20");
    }

    /// <summary>
    ///     设置 Headers 的 UA、Referer。
    ///     下载层在未指定浏览器 UA 与自定义 UA 时使用最简的 DownloadUserAgent（无括号无网址），
    ///     避免 BMCLAPI / CDN 302 跳转返回 0 字节空体。
    /// </summary>
    internal static void SecretHeadersSign(
        string url,
        ref HttpRequestMessage client,
        bool useBrowserUserAgent = false,
        string customUserAgent = "")
    {
        client.Version = HttpVersion.Version20;
        client.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsedUri)
            && (parsedUri.Host == "api.curseforge.com"
                || parsedUri.Host == "edge.forgecdn.net"
                || parsedUri.Host == "mediafilez.forgecdn.net"))
            client.Headers.Add("x-api-key", Secrets.CurseForgeAPIKey);
        var userAgent = !string.IsNullOrEmpty(customUserAgent)
            ? customUserAgent
            : useBrowserUserAgent
                ? $"PCL2/{LauncherCommon.UpstreamVersion}.{LauncherCommon.VersionBranchCode} PCLCE/{LauncherCommon.VersionStandardCode} Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36 Edg/136.0.0.0"
                : DreamPortBuildConfig.DownloadUserAgent;
        client.Headers.Add("User-Agent", userAgent);
    }
}