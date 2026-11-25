using System.Text;
using System.Text.Json;
using PCL.Core.App;
using PCL.Core.Logging;

namespace DreamPort.Core.Updates;

public enum DreamPortUpdateChannel
{
    Stable,
    Beta
}

public sealed record DreamPortUpdateAsset(
    string Name,
    string Url,
    long Size,
    string? Sha256,
    string? Platform);

public sealed record DreamPortUpdateRelease(
    string Tag,
    string Version,
    string Notes,
    string? PublishedAt,
    string Source,
    IReadOnlyList<DreamPortUpdateAsset> Assets);

public sealed record DreamPortUpdateCheckResult(
    bool HasUpdate,
    bool IsSkipped,
    string CurrentVersion,
    string? LatestVersion,
    string? Notes,
    string? DownloadUrl,
    string? Sha256,
    string? AssetName,
    string Source,
    string Message);

public sealed class DreamPortSkippedVersionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    public DreamPortSkippedVersionStore(string? dataDirectory = null)
    {
        var dir = string.IsNullOrWhiteSpace(dataDirectory) ? Paths.Data : dataDirectory;
        _filePath = Path.Combine(dir, "dreamport-update-state.json");
    }

    public string? GetSkippedVersion(DreamPortUpdateChannel channel)
    {
        var state = ReadState();
        return channel == DreamPortUpdateChannel.Beta ? state.SkippedBeta : state.SkippedStable;
    }

    public bool IsVersionSkipped(DreamPortUpdateChannel channel, string? version)
    {
        var target = NormalizeTagVersion(version);
        if (string.IsNullOrEmpty(target))
            return false;

        var skipped = NormalizeTagVersion(GetSkippedVersion(channel));
        return !string.IsNullOrEmpty(skipped) && string.Equals(skipped, target, StringComparison.OrdinalIgnoreCase);
    }

    public void SkipVersion(DreamPortUpdateChannel channel, string version)
    {
        var clean = NormalizeTagVersion(version);
        if (string.IsNullOrEmpty(clean))
            return;

        var state = ReadState();
        if (channel == DreamPortUpdateChannel.Beta)
            state.SkippedBeta = clean;
        else
            state.SkippedStable = clean;

        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(state, JsonOptions), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "DreamPort", "保存跳过更新版本号失败");
        }
    }

    public static string NormalizeTagVersion(string? raw)
    {
        var s = (raw ?? string.Empty).Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
            s = s[1..].Trim();
        return s;
    }

    private UpdateState ReadState()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var raw = File.ReadAllText(_filePath, Encoding.UTF8);
                return JsonSerializer.Deserialize<UpdateState>(raw) ?? new UpdateState();
            }
        }
        catch
        {
            // ignore corrupted state
        }

        return new UpdateState();
    }

    private sealed class UpdateState
    {
        public string? SkippedStable { get; set; }
        public string? SkippedBeta { get; set; }
    }
}
