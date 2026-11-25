using DreamPort.Core.Api;
using DreamPort.Core.Auth;
using DreamPort.Core.Services;

namespace DreamPort.UI.Services;

/// <summary>
/// 全局极简在线心跳宿主：登录后启动、登出/切站或退出时自动汇报 <c>/api/client/offline</c> 并停跳。
/// </summary>
public static class DreamPortHeartbeatHost
{
    private static readonly DreamPortSessionStore Store = new();

    private static readonly DreamPortHeartbeatService Service = new(
        getClient: CreateAuthenticatedClient,
        isLoggedIn: () => !string.IsNullOrWhiteSpace(Store.Load().Token),
        ownsClientInstances: true);

    public static void EnsureStarted(string reason = "session")
    {
        Service.Start(reason);
    }

    public static Task StopAndNotifyOfflineAsync(TimeSpan? timeout = null)
        => Service.StopAndNotifyOfflineAsync(timeout ?? TimeSpan.FromSeconds(1.5));

    private static DreamPortApiClient? CreateAuthenticatedClient()
    {
        var saved = Store.Load();
        if (string.IsNullOrWhiteSpace(saved.Token))
            return null;

        var api = new DreamPortApiClient(saved.BaseUrl);
        api.SetToken(saved.Token);
        return api;
    }
}
