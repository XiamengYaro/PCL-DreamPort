using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamPort.Core.Api;
using DreamPort.Core.Auth;
using PCL.Core.ViewModel;

namespace PCL;

/// <summary>
/// 梦港联机大厅视图模型（TCP/TLS 中继状态探测与账号连通检查；房间创建与转发在 Phase 4 接入）。
/// </summary>
public partial class PageToolsGameLinkViewModel : ViewModelBase
{
    [ObservableProperty] private string _statusHint = "正在检查梦港账号与中继服务状态...";
    [ObservableProperty] private string _accountSummary = "未检查";
    [ObservableProperty] private string _relaySummary = "等待检测站点 /api/lobby/config 配置";
    [ObservableProperty] private bool _isBusy;

    public bool IsNotBusy => !IsBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsNotBusy));

    public void Reload()
    {
        _ = RefreshStatusAsync();
    }

    [RelayCommand]
    public async Task RefreshStatusAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            var session = new DreamPortSessionStore().Load();
            if (string.IsNullOrWhiteSpace(session.Token))
            {
                StatusHint = "尚未登录梦港账号，请先在「工具 → 梦港账号」通过浏览器授权登录，以连接梦港中继服务。";
                AccountSummary = $"站点地址：{session.BaseUrl}\n当前状态：未登录";
                RelaySummary = "登录后将自动从站点拉取中继节点地址、TLS 配置与每房人数上限。";
                return;
            }

            AccountSummary = $"站点地址：{session.BaseUrl}\n当前账号：{session.Username ?? "已登录"}（{(session.IsAdmin ? "管理员" : "普通玩家")}）";

            using var api = new DreamPortApiClient(session.BaseUrl);
            api.SetToken(session.Token);

            var lobby = await api.GetLobbyConfigAsync();
            if (lobby is null)
            {
                StatusHint = "未能从站点获取联机中继配置（请检查网络连接、站点版本或登录状态）。";
                RelaySummary = "暂未获取到 /api/lobby/config 响应。";
                return;
            }

            if (!lobby.Enabled)
            {
                StatusHint = "当前站点已关闭梦港联机中继功能，请联系管理员确认服务端配置。";
                RelaySummary = $"中继启用：否\n中继主机：{lobby.RelayHost ?? "-"}";
                return;
            }

            StatusHint = $"✓ 梦港联机中继已就绪（{lobby.RelayHost}:{lobby.RelayPort}）。";
            RelaySummary = $"中继地址：{lobby.RelayHost}:{lobby.RelayPort}\n"
                + $"传输加密：{(lobby.RelayTls ? "TLS 加密" : "明文 TCP")}\n"
                + $"单房上限：{(lobby.MaxPlayersPerRoom > 0 ? $"{lobby.MaxPlayersPerRoom} 人" : "以服务端为准")}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
