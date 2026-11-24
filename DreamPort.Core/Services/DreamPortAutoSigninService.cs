using System.Text;
using System.Text.Json;
using DreamPort.Core.Api;
using PCL.Core.App;
using PCL.Core.Logging;

namespace DreamPort.Core.Services;

public sealed record DreamPortAutoSigninOutcome(
    bool Attempted,
    bool SignedNow,
    bool AlreadySignedToday,
    bool ShouldNotifyUser,
    string? NotificationText,
    DreamPortSigninResult? SigninResult);

/// <summary>
/// DreamPort 每日自动签到服务：仅对已获白名单（approved）的玩家在每个自然日静默执行一次，
/// 今日已签或网络/未审核异常时保持静默，不弹出骚扰错误提示。
/// </summary>
public sealed class DreamPortAutoSigninService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _stateFilePath;
    private readonly Func<DateOnly> _todayProvider;
    private readonly Lock _lock = new();

    public DreamPortAutoSigninService(string? dataDirectory = null, Func<DateOnly>? todayProvider = null)
    {
        var dir = string.IsNullOrWhiteSpace(dataDirectory) ? Paths.Data : dataDirectory;
        _stateFilePath = Path.Combine(dir, "dreamport-signin-state.json");
        _todayProvider = todayProvider ?? (() => DateOnly.FromDateTime(DateTime.Now));
    }

    public async Task<DreamPortAutoSigninOutcome> TryAutoSigninAsync(
        DreamPortApiClient api,
        string? username,
        string? profileStatus,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(api.CurrentToken) || string.IsNullOrWhiteSpace(username))
        {
            return new DreamPortAutoSigninOutcome(false, false, false, false, null, null);
        }

        if (!string.IsNullOrWhiteSpace(profileStatus)
            && !string.Equals(profileStatus.Trim(), "approved", StringComparison.OrdinalIgnoreCase))
        {
            return new DreamPortAutoSigninOutcome(false, false, false, false, null, null);
        }

        var todayText = _todayProvider().ToString("yyyy-MM-dd");
        var key = BuildAccountKey(api.BaseUrl, username);
        if (string.Equals(ReadLastSignedDate(key), todayText, StringComparison.Ordinal))
        {
            return new DreamPortAutoSigninOutcome(false, false, true, false, null, null);
        }

        var result = await api.SigninAsync(ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            return new DreamPortAutoSigninOutcome(true, false, false, false, null, result);
        }

        WriteLastSignedDate(key, todayText);
        if (result.First)
        {
            var pointsText = result.PointsAwarded > 0 ? $"（+{result.PointsAwarded} 积分，连签 {result.Streak} 天）" : "";
            var notice = string.IsNullOrWhiteSpace(result.Message)
                ? $"梦港每日自动签到成功{pointsText}"
                : $"梦港自动签到：{result.Message}{pointsText}";
            return new DreamPortAutoSigninOutcome(true, true, false, true, notice, result);
        }

        return new DreamPortAutoSigninOutcome(true, false, true, false, null, result);
    }

    private static string BuildAccountKey(string baseUrl, string username)
        => $"{baseUrl.Trim().TrimEnd('/').ToLowerInvariant()}|{username.Trim().ToLowerInvariant()}";

    private string? ReadLastSignedDate(string accountKey)
    {
        lock (_lock)
        {
            var map = ReadStateMapUnsafe();
            return map.TryGetValue(accountKey, out var val) ? val : null;
        }
    }

    private void WriteLastSignedDate(string accountKey, string dateText)
    {
        lock (_lock)
        {
            try
            {
                var map = ReadStateMapUnsafe();
                map[accountKey] = dateText;
                var dir = Path.GetDirectoryName(_stateFilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(map, JsonOptions), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                LogWrapper.Debug(ex, "DreamPort", "保存自动签到状态失败");
            }
        }
    }

    private Dictionary<string, string> ReadStateMapUnsafe()
    {
        try
        {
            if (!File.Exists(_stateFilePath))
                return new Dictionary<string, string>(StringComparer.Ordinal);
            var raw = File.ReadAllText(_stateFilePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(raw)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
