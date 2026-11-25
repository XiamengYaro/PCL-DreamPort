using PCL.Core.Logging;
using PCL.Core.Platform;

namespace PCL;

/// <summary>日志中敏感信息的打码。</summary>
public interface ILogFilter
{
    string FilterUserName(string raw, char filterChar);
    string FilterAccessToken(string raw, char filterChar);
}

public static class LogFilter
{
    public static ILogFilter? Current => ApplicationRuntime.GetService<ILogFilter>();

    /// <summary>当前登录结果（尚未登录时为 <c>null</c>）。只读，供 UI 层日志打码等场景使用。</summary>
    public static GameLaunch.McLoginResult? CurrentLogin => GameLaunch.mcLoginLoader.output;

    public static string FilterUserName(string raw, char filterChar)
    {
        try
        {
            if (Current is { } current)
                return current.FilterUserName(raw, filterChar);
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "LogFilter", "用户名脱敏服务失败，已使用内置规则");
        }

        return SensitiveDataFilter.FilterUserName(raw, filterChar);
    }

    public static string FilterAccessToken(string raw, char filterChar)
    {
        var login = CurrentLogin;
        try
        {
            if (Current is { } current)
                return current.FilterAccessToken(raw, filterChar);
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "LogFilter", "AccessToken 脱敏服务失败，已使用内置规则");
        }

        return SensitiveDataFilter.FilterAccessToken(
            raw,
            filterChar,
            login?.AccessToken,
            login?.Uuid);
    }
}

/// <summary>区域功能校验接口（已放行中国大陆及所有地区的下载、补全与整合包安装）。</summary>
public interface IRestrictedFeatures
{
    bool IsRestricted { get; }
    bool Check(string feature);
    void ThrowIfRestricted(string feature);
}

public static class RestrictedFeatures
{
    public static IRestrictedFeatures? Current =>
        ApplicationRuntime.GetService<IRestrictedFeatures>();

    public static bool IsRestricted => false;

    public static bool Check(string feature) => true;

    public static void ThrowIfRestricted(string feature)
    {
    }
}