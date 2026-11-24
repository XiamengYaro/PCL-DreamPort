using DreamPort.Core.Api;
using DreamPort.Core.Config;

namespace DreamPort.Core.Homepage;

/// <summary>
/// DreamPort 原生自定义主页服务：管理内置兜底 XAML 原型与远程 <c>/api/client/pcl-homepage</c> 地址解析。
/// </summary>
public static class DreamPortHomepageService
{
    private const string PrototypeResourceName = "DreamPort.Core.Resources.DreamPortHomepagePrototype.xaml";
    private static readonly Lazy<string> PrototypeXamlCache = new(LoadPrototypeXaml);

    /// <summary>获取内置的 PCL XAML 主页原型（当未接入后端或拉取失败时兜底使用）。</summary>
    public static string GetPrototypeXaml() => PrototypeXamlCache.Value;

    /// <summary>拼装服务端推送自定义主页接口地址。</summary>
    public static string GetRemoteHomepageUrl(string? baseUrl = null)
    {
        var resolvedBase = DreamPortApiClient.TryNormalizeBaseUrl(baseUrl, out var normalized, out _)
            ? normalized
            : DreamPortBuildConfig.BaseUrl;
        return resolvedBase + "/api/client/pcl-homepage";
    }

    private static string LoadPrototypeXaml()
    {
        var assembly = typeof(DreamPortHomepageService).Assembly;
        using var stream = assembly.GetManifestResourceStream(PrototypeResourceName)
            ?? throw new InvalidOperationException($"未找到内置主页原型资源：{PrototypeResourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
