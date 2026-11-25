using System.Collections.Concurrent;
using System.Text;
using DreamPort.Core.Api;

namespace DreamPort.Core.Services;

/// <summary>
/// 社区资源当页 AI 翻译与模组/整合包功能 AI 解析助手（内置有界内存缓存，同一资源不重复消耗配额）。
/// </summary>
public sealed class DreamPortResourceAiHelper
{
    public const int MaxCachedEntries = 1000;

    public static DreamPortResourceAiHelper Shared { get; } = new();

    private readonly ConcurrentDictionary<string, DreamPortResourceTranslatedItem> _translateCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DreamPortResourceExplainResult> _explainCache = new(StringComparer.Ordinal);

    public async Task<DreamPortResourceTranslateResult> TranslatePageAsync(
        DreamPortApiClient api,
        IReadOnlyList<DreamPortResourceTranslateItem> items,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        var normalized = (items ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .Take(30)
            .ToArray();

        if (normalized.Length == 0)
            return new DreamPortResourceTranslateResult(true, new Dictionary<string, DreamPortResourceTranslatedItem>(), 0);

        var output = new Dictionary<string, DreamPortResourceTranslatedItem>(StringComparer.Ordinal);
        var misses = new List<DreamPortResourceTranslateItem>();
        var localHits = 0;

        foreach (var item in normalized)
        {
            var key = BuildTranslateCacheKey(item);
            if (_translateCache.TryGetValue(key, out var hit))
            {
                output[item.Id.Trim()] = hit;
                localHits++;
            }
            else
            {
                misses.Add(item);
            }
        }

        if (misses.Count == 0)
            return new DreamPortResourceTranslateResult(true, output, localHits);

        var remote = await api.TranslateResourcePageAsync(misses, ct).ConfigureAwait(false);
        foreach (var kv in remote.Translations)
        {
            output[kv.Key] = kv.Value;
            var match = misses.FirstOrDefault(m => string.Equals(m.Id.Trim(), kv.Key, StringComparison.Ordinal));
            if (match is not null)
                StoreBounded(_translateCache, BuildTranslateCacheKey(match), kv.Value);
        }

        return new DreamPortResourceTranslateResult(
            Ok: remote.Ok || output.Count > 0,
            Translations: output,
            CacheHits: localHits + remote.CacheHits,
            Message: remote.Message,
            FailureReason: remote.FailureReason);
    }

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

    private static string BuildTranslateCacheKey(DreamPortResourceTranslateItem item)
        => $"{item.Id.Trim()}\n{item.Title}\n{item.Summary}";

    private static string BuildExplainCacheKey(DreamPortResourceExplainRequest req)
        => $"{req.Kind}|{req.Name}|{req.Summary}|{req.Description}|{string.Join(",", req.Categories)}|{string.Join(",", req.Mods)}";

    private static void StoreBounded<TVal>(ConcurrentDictionary<string, TVal> dict, string key, TVal val)
    {
        if (dict.Count >= MaxCachedEntries)
            dict.Clear();
        dict[key] = val;
    }
}
