using System.Collections.Concurrent;
using System.Text;
using DreamPort.Core.Api;

namespace DreamPort.Core.Services;

/// <summary>
/// 模组/整合包功能 AI 解析助手（内置有界内存缓存，同一资源不重复消耗配额）。
/// </summary>
public sealed class DreamPortResourceAiHelper
{
    public const int MaxCachedEntries = 1000;

    public static DreamPortResourceAiHelper Shared { get; } = new();

    private readonly ConcurrentDictionary<string, DreamPortResourceExplainResult> _explainCache = new(StringComparer.Ordinal);

    public async Task<DreamPortResourceExplainResult> ExplainAsync(
        DreamPortApiClient api,
        DreamPortResourceExplainRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(request);

        var cacheKey = BuildExplainCacheKey(request);
        if (_explainCache.TryGetValue(cacheKey, out var cached))
            return cached with { Cached = true };

        var remote = await api.ExplainResourceAsync(request, ct).ConfigureAwait(false);
        if (remote.Ok)
            StoreBounded(_explainCache, cacheKey, remote);

        return remote;
    }

    public static string FormatExplainForDisplay(string resourceName, DreamPortResourceExplainResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"资源：{resourceName}");
        if (!string.IsNullOrWhiteSpace(result.Overview))
        {
            sb.AppendLine();
            sb.AppendLine($"一句话定位：{result.Overview}");
        }

        if (result.Features.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("主要玩法与功能：");
            foreach (var f in result.Features)
                sb.AppendLine($"- {f}");
        }

        if (result.Notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("前置与注意事项：");
            foreach (var n in result.Notes)
                sb.AppendLine($"- {n}");
        }

        return sb.ToString().Trim();
    }

    private static string BuildExplainCacheKey(DreamPortResourceExplainRequest req)
        => $"{req.Kind}|{req.Name}|{req.Summary}|{req.Description}|{string.Join(",", req.Categories)}|{string.Join(",", req.Mods)}";

    private static void StoreBounded<TVal>(ConcurrentDictionary<string, TVal> dict, string key, TVal val)
    {
        if (dict.Count >= MaxCachedEntries)
            dict.Clear();
        dict[key] = val;
    }
}
