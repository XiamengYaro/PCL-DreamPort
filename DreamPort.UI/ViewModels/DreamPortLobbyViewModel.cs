using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamPort.Core.Api;
using DreamPort.Core.Auth;
using DreamPort.Core.Config;
using DreamPort.Core.Lobby;
using DreamPort.UI.Services;

namespace DreamPort.UI.ViewModels;

public sealed record DreamPortLobbyRoomItemVm(
    string Code,
    string Name,
    string Host,
    string VisibilityLabel,
    string VersionSummary,
    string OccupancyText,
    bool HostGone,
    ICommand JoinCommand)
{
    public string HeaderText => HostGone ? $"{Name}（房主暂离）" : Name;

    public string DetailText =>
        $"房码：{Code} · 房主：{Host} · {VersionSummary} · {OccupancyText} · {VisibilityLabel}";
}

public sealed record DreamPortLobbyMemberItemVm(
    string PeerId,
    string PlayerName,
    bool IsHost,
    bool Kicked,
    long JoinedAtMs,
    ICommand KickCommand,
    ICommand UnkickCommand)
{
    public bool CanKick => !IsHost && !Kicked;
    public bool CanUnkick => !IsHost && Kicked;

    public string HeaderText => IsHost
        ? $"{PlayerName}（房主）"
        : Kicked
            ? $"{PlayerName}（已移出拉黑）"
            : PlayerName;

    public string MetaText => JoinedAtMs > 0
        ? $"加入时间：{DateTimeOffset.FromUnixTimeMilliseconds(JoinedAtMs).ToLocalTime():HH:mm:ss} · ID: {PeerId}"
        : $"标识：{PeerId}";
}

public sealed record DreamPortLobbyInviteItemVm(
    long Id,
    string Code,
    string FromUser,
    string ToUser,
    string? Message,
    string RoomDisplay,
    long ExpiresAtMs,
    bool IsIncoming,
    ICommand? AcceptCommand,
    ICommand? RejectCommand,
    ICommand? CancelCommand)
{
    public string HeaderText => IsIncoming
        ? $"{FromUser} 邀请你加入「{RoomDisplay}」"
        : $"已邀请 {ToUser}（房码 {Code}）";

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    public string DetailText => ExpiresAtMs > 0
        ? $"房码：{Code} · 有效期至 {DateTimeOffset.FromUnixTimeMilliseconds(ExpiresAtMs).ToLocalTime():HH:mm}"
        : $"房码：{Code}";
}

/// <summary>
/// 梦港联机大厅视图模型（TCP/TLS 中继 + 局域网广播 + 房间/成员/好友邀请管理）。
/// </summary>
public partial class DreamPortLobbyViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DreamPortSessionStore _store;
    private readonly Func<string, DreamPortApiClient> _clientFactory;
    private readonly DreamPortLobbyTunnel _tunnel;
    private readonly Func<TimeSpan, CancellationToken, Task<(int? Port, string? ErrorMessage)>> _detectLanPort;
    private readonly Func<string, Task<bool>> _copyToClipboard;
    private readonly Func<(string? Version, string? Loader)>? _getSelectedInstanceInfo;
    private readonly Action? _goToAccount;
    private readonly bool _registerGlobalLifecycle;
    private readonly SynchronizationContext? _uiCtx;
    private readonly Func<Task> _globalShutdownDelegate;

    private DreamPortApiClient _api;
    private string _baseUrl = DreamPortBuildConfig.BaseUrl;
    private DreamPortLobbyConfigResult? _relayConfig;
    private CancellationTokenSource? _detectCts;
    private CancellationTokenSource? _statsCts;
    private DreamPortLobbyJoinResult? _pendingJoinResult;
    private bool _loadedOnce;

    [ObservableProperty] private string _statusHint = "正在检查梦港账号与中继服务状态...";
    [ObservableProperty] private DreamPortHintLevel _hintLevel = DreamPortHintLevel.Info;
    [ObservableProperty] private string _accountSummary = "未检查";
    [ObservableProperty] private string _relaySummary = "等待检测站点 /api/lobby/config 配置";
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private string _currentUsername = string.Empty;
    [ObservableProperty] private bool _isRelayEnabled;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isDetectingPort;
    [ObservableProperty] private bool _isHosting;
    [ObservableProperty] private bool _isGuest;

    // ── 残留房间自救 ──
    [ObservableProperty] private bool _hasStaleRoom;
    [ObservableProperty] private string _staleRoomCode = string.Empty;
    [ObservableProperty] private string _staleRoomSummary = string.Empty;

    // ── 开房配置与房主状态 ──
    [ObservableProperty] private string _roomNameInput = string.Empty;
    [ObservableProperty] private int _visibilityIndex; // 0: public, 1: private
    [ObservableProperty] private string _localPortInput = string.Empty;
    [ObservableProperty] private string _mcVersionInput = string.Empty;
    [ObservableProperty] private string _modLoaderInput = string.Empty;
    [ObservableProperty] private string _activeRoomCode = string.Empty;
    [ObservableProperty] private string _activeRoomName = string.Empty;
    [ObservableProperty] private string _hostOccupancyText = "1 / 8 人";
    [ObservableProperty] private string _hostGamePortStatusText = "本机游戏端口检测中...";
    [ObservableProperty] private string _inviteFriendUsernameInput = string.Empty;
    [ObservableProperty] private string _inviteMessageInput = string.Empty;
    [ObservableProperty] private string? _selectedFriend;

    // ── 加房配置与访客状态 ──
    [ObservableProperty] private string _joinRoomCodeInput = string.Empty;
    [ObservableProperty] private string _preferLocalPortInput = string.Empty;
    [ObservableProperty] private string _guestLocalAddress = string.Empty;
    [ObservableProperty] private string _guestAdvertiseHint = "已在局域网广播房间，也可直接在 Minecraft「多人游戏」使用上方本地地址直连。";
    [ObservableProperty] private bool _isHostGone;
    [ObservableProperty] private string _hostGoneBannerText = string.Empty;
    [ObservableProperty] private bool _hasPendingVersionMismatch;
    [ObservableProperty] private string _versionMismatchMessage = string.Empty;

    // ── 共享活跃数据面板 ──
    [ObservableProperty] private string _trafficSummaryText = "活跃数据连接：0 | 收：0 B | 发：0 B";
    [ObservableProperty] private string _publicRoomsSummaryText = "暂无公开房间。";

    public ObservableCollection<DreamPortLobbyRoomItemVm> PublicRooms { get; } = [];
    public ObservableCollection<DreamPortLobbyMemberItemVm> Members { get; } = [];
    public ObservableCollection<DreamPortLobbyInviteItemVm> IncomingInvites { get; } = [];
    public ObservableCollection<DreamPortLobbyInviteItemVm> OutgoingInvites { get; } = [];
    public ObservableCollection<string> FriendOptions { get; } = [];
    public ObservableCollection<string> EventLogs { get; } = [];

    public DreamPortLobbyViewModel(
        DreamPortSessionStore? store = null,
        Func<string, DreamPortApiClient>? clientFactory = null,
        DreamPortLobbyTunnel? tunnel = null,
        Func<TimeSpan, CancellationToken, Task<(int? Port, string? ErrorMessage)>>? detectLanPort = null,
        Func<string, Task<bool>>? copyToClipboard = null,
        Func<(string? Version, string? Loader)>? getSelectedInstanceInfo = null,
        Action? goToAccount = null,
        bool registerGlobalLifecycle = false)
    {
        _store = store ?? new DreamPortSessionStore();
        _clientFactory = clientFactory ?? (url => new DreamPortApiClient(url));
        _tunnel = tunnel ?? new DreamPortLobbyTunnel();
        _detectLanPort = detectLanPort ?? DreamPortLanService.DetectLanPortAsync;
        _copyToClipboard = copyToClipboard ?? (_ => Task.FromResult(false));
        _getSelectedInstanceInfo = getSelectedInstanceInfo;
        _goToAccount = goToAccount;
        _registerGlobalLifecycle = registerGlobalLifecycle;
        _uiCtx = SynchronizationContext.Current;
        _globalShutdownDelegate = ShutdownActiveForLifecycleAsync;

        var saved = _store.Load();
        _baseUrl = saved.BaseUrl;
        _api = _clientFactory(_baseUrl);
        if (!string.IsNullOrEmpty(saved.Token))
            _api.SetToken(saved.Token);

        PrefillFromSelectedInstance();
        if (_registerGlobalLifecycle)
            DreamPortLobbyLifecycle.RegisterActiveShutdown(_globalShutdownDelegate);
    }

    public bool IsNotBusy => !IsBusy;
    public bool IsIdle => !IsHosting && !IsGuest;
    public bool IsInActiveSession => IsHosting || IsGuest;
    public bool CanHostOrJoin => IsLoggedIn && IsRelayEnabled && IsIdle && IsNotBusy;
    public bool HasPublicRooms => PublicRooms.Count > 0;
    public bool HasNoPublicRooms => PublicRooms.Count == 0;
    public bool HasMembers => Members.Count > 0;
    public bool HasIncomingInvites => IncomingInvites.Count > 0;
    public bool HasOutgoingInvites => OutgoingInvites.Count > 0;
    public bool HasFriendOptions => FriendOptions.Count > 0;
    public bool HasEventLogs => EventLogs.Count > 0;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotBusy));
        OnPropertyChanged(nameof(CanHostOrJoin));
    }

    partial void OnIsLoggedInChanged(bool value) => OnPropertyChanged(nameof(CanHostOrJoin));
    partial void OnIsRelayEnabledChanged(bool value) => OnPropertyChanged(nameof(CanHostOrJoin));

    partial void OnIsHostingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsInActiveSession));
        OnPropertyChanged(nameof(CanHostOrJoin));
    }

    partial void OnIsGuestChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsInActiveSession));
        OnPropertyChanged(nameof(CanHostOrJoin));
    }

    partial void OnSelectedFriendChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            InviteFriendUsernameInput = value;
    }

    public void PrefillFromSelectedInstance()
    {
        if (_getSelectedInstanceInfo is null)
            return;

        try
        {
            var (ver, loader) = _getSelectedInstanceInfo();
            if (string.IsNullOrWhiteSpace(McVersionInput) && !string.IsNullOrWhiteSpace(ver))
                McVersionInput = ver.Trim();
            if (string.IsNullOrWhiteSpace(ModLoaderInput) && !string.IsNullOrWhiteSpace(loader))
                ModLoaderInput = loader.Trim();
        }
        catch
        {
            // 实例信息未初始化时静默忽略
        }
    }

    public void Reload()
    {
        _ = RefreshStatusAsync();
    }

    public async Task InitializeOrSyncAsync(CancellationToken ct = default)
    {
        var saved = _store.Load();
        var siteChanged = !string.Equals(_baseUrl, saved.BaseUrl, StringComparison.Ordinal);
        var tokenChanged = !string.Equals(_api.CurrentToken, saved.Token, StringComparison.Ordinal);
        if (_loadedOnce && !siteChanged && !tokenChanged)
            return;

        _loadedOnce = true;
        await RefreshStatusAsync(ct);
    }

    [RelayCommand]
    public async Task RefreshStatusAsync(CancellationToken ct = default)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            SyncSessionFromStore();
            PrefillFromSelectedInstance();

            var publicTask = RefreshPublicRoomsCoreAsync(ct);
            var cfgTask = _api.GetLobbyConfigAsync(ct);
            await Task.WhenAll(publicTask, cfgTask);

            var cfg = await cfgTask;
            _relayConfig = cfg;
            UpdateRelaySummary(cfg);

            if (!IsLoggedIn)
            {
                HasStaleRoom = false;
                IncomingInvites.Clear();
                OutgoingInvites.Clear();
                FriendOptions.Clear();
                NotifyCollectionStates();
                SetStatus("尚未登录梦港账号（仅可浏览下方公开大厅）；登录后可创建或加入房间。", DreamPortHintLevel.Warning);
                return;
            }

            if (IsIdle)
            {
                var myTask = RefreshStaleRoomCoreAsync(ct);
                var invTask = RefreshInvitesCoreAsync(ct);
                var friendTask = RefreshFriendsCoreAsync(ct);
                await Task.WhenAll(myTask, invTask, friendTask);
            }
            else if (IsHosting)
            {
                await Task.WhenAll(RefreshInvitesCoreAsync(ct), RefreshFriendsCoreAsync(ct));
                if (_tunnel.CurrentHostSession is { } hs)
                    await hs.RefreshMembersAsync(ct);
            }

            if (IsHosting)
                SetStatus($"✓ 正在开房「{ActiveRoomName}」（房码：{ActiveRoomCode}）。", DreamPortHintLevel.Info);
            else if (IsGuest)
                SetStatus($"✓ 已加入联机房间「{ActiveRoomName}」，本地直连地址：{GuestLocalAddress}", DreamPortHintLevel.Info);
            else if (HasStaleRoom)
                SetStatus($"检测到未关闭的旧房间（{StaleRoomCode}），请先点击「关闭残留旧房」后再开新房。", DreamPortHintLevel.Warning);
            else if (cfg is { Enabled: true })
                SetStatus($"✓ 梦港联机中继已就绪（{cfg.RelayHost}:{cfg.RelayPort}）。", DreamPortHintLevel.Info);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void GoToDreamPortAccount()
    {
        _goToAccount?.Invoke();
    }

    [RelayCommand]
    private async Task CloseStaleRoomAsync()
    {
        if (!IsLoggedIn || string.IsNullOrWhiteSpace(StaleRoomCode))
            return;

        IsBusy = true;
        try
        {
            var res = await _api.CloseLobbyRoomAsync(StaleRoomCode);
            if (!res.Ok)
            {
                SetStatus(res.Message, DreamPortHintLevel.Error);
                return;
            }

            HasStaleRoom = false;
            StaleRoomCode = string.Empty;
            StaleRoomSummary = string.Empty;
            AddEventLog("已关闭上次遗留的联机房间。");
            SetStatus($"✓ {res.Message}", DreamPortHintLevel.Info);
            await RefreshPublicRoomsCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DetectLanPortAsync()
    {
        CancelDetectLanPortInternal();
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        _detectCts = cts;
        IsDetectingPort = true;
        SetStatus("正在监听局域网世界广播（224.0.2.60:4445，最多 15 秒）...", DreamPortHintLevel.Info);

        try
        {
            var (port, err) = await _detectLanPort(TimeSpan.FromSeconds(15), token);
            if (port is { } found)
            {
                LocalPortInput = found.ToString();
                AddEventLog($"检测到本机局域网开放端口：{found}");
                SetStatus($"✓ 已检测到本地游戏局域网端口：{found}，可直接点击「创建房间」。", DreamPortHintLevel.Info);
            }
            else
            {
                SetStatus(err ?? "未检测到开放的局域网世界，请在游戏内开启局域网或手动填写端口。", DreamPortHintLevel.Warning);
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _detectCts, null, cts);
            cts.Dispose();
            IsDetectingPort = false;
        }
    }

    [RelayCommand]
    private void CancelDetectLanPort()
    {
        CancelDetectLanPortInternal();
        IsDetectingPort = false;
        SetStatus("已取消局域网端口检测。", DreamPortHintLevel.Info);
    }

    [RelayCommand]
    private async Task CreateAndHostRoomAsync()
    {
        SyncSessionFromStore();
        if (!IsLoggedIn)
        {
            SetStatus("请先登录梦港账号后再创建联机房间。", DreamPortHintLevel.Error);
            return;
        }

        if (_tunnel.IsBusy || IsInActiveSession)
        {
            SetStatus("当前已在一个联机房间中，请先结束当前联机。", DreamPortHintLevel.Warning);
            return;
        }

        var localPort = DreamPortLanService.ParseLocalPort(LocalPortInput);
        if (localPort is null)
        {
            SetStatus("请先点击「自动检测游戏端口」或输入有效的本地游戏端口（1-65535）。", DreamPortHintLevel.Error);
            return;
        }

        var roomName = string.IsNullOrWhiteSpace(RoomNameInput)
            ? $"{(string.IsNullOrWhiteSpace(CurrentUsername) ? "玩家" : CurrentUsername)}的房间"
            : RoomNameInput.Trim();
        if (roomName.Length > 24)
            roomName = roomName[..24];

        IsBusy = true;
        try
        {
            var cfg = await EnsureRelayEndpointAsync();
            if (cfg is null)
                return;

            // 先探活本地游戏端口，避免建出服务端房间后才发现游戏未开局域网而留下陈旧房间
            if (!await DreamPortLanService.PingLocalPortAsync(localPort.Value, 1500))
            {
                SetStatus($"本机 {localPort.Value} 端口暂不可连，请先在 Minecraft 中点击「对局域网开放」后再试。", DreamPortHintLevel.Error);
                return;
            }

            var visibility = VisibilityIndex == 1 ? "private" : "public";
            var createRes = await _api.CreateLobbyRoomAsync(
                name: roomName,
                visibility: visibility,
                mcVersion: McVersionInput,
                modLoader: ModLoaderInput);

            if (!createRes.Ok || createRes.Room is null || string.IsNullOrWhiteSpace(createRes.Ticket))
            {
                if (createRes.FailureReason == DreamPortFailureReason.Expired)
                    HandleSessionExpired(createRes.Message ?? "登录已过期，请重新登录。");
                else
                    SetStatus(createRes.Message ?? "创建房间失败。", DreamPortHintLevel.Error);
                return;
            }

            var room = createRes.Room;
            var endpoint = new DreamPortRelayEndpoint(cfg.RelayHost!, cfg.RelayPort, cfg.RelayTls);

            try
            {
                await _tunnel.StartHostAsync(
                    endpoint: endpoint,
                    roomCode: room.Code,
                    ticket: createRes.Ticket,
                    playerName: CurrentUsername,
                    localGamePort: localPort.Value,
                    onEvent: ev => DispatchUi(() => HandleHostTunnelEvent(ev)),
                    onMembersChanged: members => DispatchUi(() => ApplyHostMembers(members)),
                    onGameAliveChanged: alive => DispatchUi(() =>
                        HostGamePortStatusText = alive
                            ? $"本地游戏端口 {localPort.Value} 运行正常"
                            : $"警告：本地游戏端口 {localPort.Value} 已关闭（中继保留 120 秒宽限期）"));
            }
            catch (Exception ex)
            {
                try { await _api.CloseLobbyRoomAsync(room.Code); } catch { /* rollback room */ }
                SetStatus($"连接梦港中继失败：{ex.Message}", DreamPortHintLevel.Error);
                return;
            }

            ActiveRoomCode = room.Code;
            ActiveRoomName = room.Name;
            HasStaleRoom = false;
            StaleRoomCode = string.Empty;
            IsHosting = true;
            HostGamePortStatusText = $"本地游戏端口 {localPort.Value} 运行正常";
            HostOccupancyText = $"1 / {Math.Max(1, room.MaxPlayers)} 人";
            Members.Clear();
            NotifyCollectionStates();
            StartStatsTicker();
            AddEventLog($"成功开房「{room.Name}」（房码 {room.Code}，本地端口 {localPort.Value}）");
            SetStatus($"✓ 房间已开启！房码：{room.Code}（发送给好友或在公开大厅可见）", DreamPortHintLevel.Info);

            await Task.WhenAll(RefreshPublicRoomsCoreAsync(), RefreshFriendsCoreAsync(), RefreshInvitesCoreAsync());
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StopHostingAsync()
    {
        if (!IsHosting)
            return;

        IsBusy = true;
        try
        {
            await StopHostingInternalAsync(closeOnServer: true);
            SetStatus("✓ 已停止开房并关闭联机房间。", DreamPortHintLevel.Info);
            await RefreshPublicRoomsCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task JoinByCodeAsync() => JoinByCodeCoreAsync(JoinRoomCodeInput, forceJoin: false);

    [RelayCommand]
    private async Task ConfirmForceJoinAsync()
    {
        var pending = _pendingJoinResult;
        _pendingJoinResult = null;
        HasPendingVersionMismatch = false;
        if (pending is null)
            return;

        IsBusy = true;
        try
        {
            await StartGuestWithJoinResultAsync(pending);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelForceJoin()
    {
        _pendingJoinResult = null;
        HasPendingVersionMismatch = false;
        VersionMismatchMessage = string.Empty;
        SetStatus("已取消加入版本不一致的房间。", DreamPortHintLevel.Info);
    }

    [RelayCommand]
    private async Task LeaveRoomAsync()
    {
        if (!IsGuest)
            return;

        IsBusy = true;
        try
        {
            await StopGuestInternalAsync();
            AddEventLog("已主动离开联机房间。");
            SetStatus("已离开当前联机房间。", DreamPortHintLevel.Info);
            await RefreshPublicRoomsCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task RefreshMembersAsync()
        => _tunnel.CurrentHostSession?.RefreshMembersAsync() ?? Task.CompletedTask;

    [RelayCommand]
    private async Task RefreshFriendsAsync()
    {
        if (!IsLoggedIn)
            return;
        IsBusy = true;
        try
        {
            await RefreshFriendsCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SendFriendInviteAsync()
    {
        if (!IsHosting || string.IsNullOrWhiteSpace(ActiveRoomCode))
        {
            SetStatus("请先开启联机房间后再邀请好友。", DreamPortHintLevel.Warning);
            return;
        }

        var friend = InviteFriendUsernameInput.Trim();
        if (string.IsNullOrEmpty(friend))
        {
            SetStatus("请选择或输入要邀请的好友用户名。", DreamPortHintLevel.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            var res = await _api.InviteFriendToRoomAsync(ActiveRoomCode, friend, InviteMessageInput);
            if (!res.Ok)
            {
                SetStatus(res.Message, DreamPortHintLevel.Error);
                return;
            }

            InviteMessageInput = string.Empty;
            AddEventLog($"已邀请好友 {friend} 加入房间");
            SetStatus($"✓ {res.Message}", DreamPortHintLevel.Info);
            await RefreshInvitesCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CopyRoomCodeAsync()
    {
        if (string.IsNullOrWhiteSpace(ActiveRoomCode))
            return;

        var ok = await _copyToClipboard(ActiveRoomCode);
        SetStatus(ok ? $"✓ 已复制房间码：{ActiveRoomCode}" : $"请手动复制房间码：{ActiveRoomCode}", DreamPortHintLevel.Info);
    }

    [RelayCommand]
    private async Task CopyLocalAddressAsync()
    {
        if (string.IsNullOrWhiteSpace(GuestLocalAddress))
            return;

        var ok = await _copyToClipboard(GuestLocalAddress);
        SetStatus(ok ? $"✓ 已复制直连地址：{GuestLocalAddress}" : $"请在游戏中使用地址：{GuestLocalAddress}", DreamPortHintLevel.Info);
    }

    public async Task ShutdownActiveForLifecycleAsync()
    {
        await StopHostingInternalAsync(closeOnServer: true).ConfigureAwait(false);
        await StopGuestInternalAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        CancelDetectLanPortInternal();
        StopStatsTicker();
        if (_registerGlobalLifecycle)
            DreamPortLobbyLifecycle.ClearActiveShutdown(_globalShutdownDelegate);
        await _tunnel.DisposeAsync().ConfigureAwait(false);
        _api.Dispose();
    }

    private async Task JoinByCodeCoreAsync(string? rawCode, bool forceJoin)
    {
        SyncSessionFromStore();
        if (!IsLoggedIn)
        {
            SetStatus("请先登录梦港账号后再加入联机房间。", DreamPortHintLevel.Error);
            return;
        }

        if (_tunnel.IsBusy || IsInActiveSession)
        {
            SetStatus("当前已在一个联机房间中，请先离开或结束当前房间。", DreamPortHintLevel.Warning);
            return;
        }

        var normalized = DreamPortApiClient.NormalizeRoomCode(rawCode);
        if (!DreamPortApiClient.IsValidRoomCodeFormat(normalized))
        {
            SetStatus("房间码格式无效，应为 8 位数字/大写字母组合（不含 I/O/L/U）。", DreamPortHintLevel.Error);
            return;
        }

        IsBusy = true;
        HasPendingVersionMismatch = false;
        _pendingJoinResult = null;
        try
        {
            var cfg = await EnsureRelayEndpointAsync();
            if (cfg is null)
                return;

            var joinRes = await _api.JoinLobbyRoomAsync(normalized, mcVersion: McVersionInput);
            if (!joinRes.Ok || joinRes.Room is null || string.IsNullOrWhiteSpace(joinRes.Ticket))
            {
                if (joinRes.FailureReason == DreamPortFailureReason.Expired)
                    HandleSessionExpired(joinRes.Message ?? "登录已过期，请重新登录。");
                else
                    SetStatus(joinRes.Message ?? "加入房间失败。", DreamPortHintLevel.Error);
                return;
            }

            if (!forceJoin && joinRes.Diff is { VersionMismatch: true } diff)
            {
                _pendingJoinResult = joinRes;
                VersionMismatchMessage =
                    $"目标房间版本为「{diff.RoomVersion}」，你当前的游戏版本为「{diff.MyVersion}」。版本不一致通常无法进入游戏，是否仍要继续建立隧道？";
                HasPendingVersionMismatch = true;
                SetStatus("检测到游戏版本不一致，请在下方确认是否仍要加入。", DreamPortHintLevel.Warning);
                return;
            }

            await StartGuestWithJoinResultAsync(joinRes);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StartGuestWithJoinResultAsync(DreamPortLobbyJoinResult joinRes)
    {
        var cfg = _relayConfig;
        if (cfg is null || !cfg.Enabled || string.IsNullOrWhiteSpace(cfg.RelayHost))
        {
            cfg = await EnsureRelayEndpointAsync();
            if (cfg is null)
                return;
        }

        var room = joinRes.Room!;
        var preferPort = DreamPortLanService.ParseLocalPort(PreferLocalPortInput) ?? 0;
        var endpoint = new DreamPortRelayEndpoint(cfg.RelayHost!, cfg.RelayPort, cfg.RelayTls);

        DreamPortGuestSession session;
        try
        {
            session = await _tunnel.StartGuestAsync(
                endpoint: endpoint,
                roomCode: room.Code,
                ticket: joinRes.Ticket!,
                playerName: CurrentUsername,
                preferLocalPort: preferPort,
                advertiseName: $"{room.Name} ({room.Host})",
                onEvent: ev => DispatchUi(() => HandleGuestTunnelEvent(ev)));
        }
        catch (Exception ex)
        {
            SetStatus($"连接梦港中继失败：{ex.Message}", DreamPortHintLevel.Error);
            return;
        }

        ActiveRoomCode = room.Code;
        ActiveRoomName = room.Name;
        GuestLocalAddress = $"127.0.0.1:{session.LocalPort}";
        IsHostGone = room.HostGone;
        HostGoneBannerText = room.HostGone ? "房主暂时不在游戏中，等待房主回到世界..." : string.Empty;
        IsGuest = true;
        JoinRoomCodeInput = room.Code;
        StartStatsTicker();
        AddEventLog($"已加入房间「{room.Name}」，本地转发地址 {GuestLocalAddress}");
        SetStatus($"✓ 已加入「{room.Name}」！可在游戏多人模式查看局域网房间，或直连 {GuestLocalAddress}", DreamPortHintLevel.Info);
    }

    private async Task<DreamPortLobbyConfigResult?> EnsureRelayEndpointAsync()
    {
        var cfg = _relayConfig ?? await _api.GetLobbyConfigAsync();
        _relayConfig = cfg;
        UpdateRelaySummary(cfg);

        if (cfg is null)
        {
            SetStatus("未能从站点获取联机中继节点配置（/api/lobby/config）。", DreamPortHintLevel.Error);
            return null;
        }

        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.RelayHost) || cfg.RelayPort <= 0)
        {
            SetStatus("当前站点未开启或未配置梦港联机中继服务。", DreamPortHintLevel.Error);
            return null;
        }

        return cfg;
    }

    private void UpdateRelaySummary(DreamPortLobbyConfigResult? cfg)
    {
        if (cfg is null)
        {
            IsRelayEnabled = false;
            RelaySummary = "暂未获取到站点 /api/lobby/config 配置。";
            return;
        }

        IsRelayEnabled = cfg.Enabled && !string.IsNullOrWhiteSpace(cfg.RelayHost) && cfg.RelayPort > 0;
        RelaySummary = cfg.Enabled
            ? $"中继地址：{cfg.RelayHost}:{cfg.RelayPort} | {(cfg.RelayTls ? "TLS 加密" : "明文 TCP")} | 每房上限：{(cfg.MaxPlayersPerRoom > 0 ? $"{cfg.MaxPlayersPerRoom} 人" : "8 人")}"
            : "当前站点管理员已关闭梦港联机功能。";
    }

    private async Task RefreshPublicRoomsCoreAsync(CancellationToken ct = default)
    {
        var res = await _api.GetLobbyRoomsAsync(ct);
        if (!res.Ok)
            return;

        PublicRooms.Clear();
        foreach (var room in res.Rooms)
        {
            var occ = room.Occupancy is { } n ? $"{n}/{room.MaxPlayers} 人" : $"人数未知（上限 {room.MaxPlayers} 人）";
            var ver = string.IsNullOrWhiteSpace(room.McVersion) ? "全版本" : room.McVersion;
            var loader = string.IsNullOrWhiteSpace(room.ModLoader)
                || string.Equals(room.ModLoader, "vanilla", StringComparison.OrdinalIgnoreCase)
                ? "原版"
                : room.ModLoader;
            var vis = string.Equals(room.Visibility, "private", StringComparison.OrdinalIgnoreCase) ? "私密" : "公开";
            var code = room.Code;

            PublicRooms.Add(new DreamPortLobbyRoomItemVm(
                Code: code,
                Name: room.Name,
                Host: room.Host,
                VisibilityLabel: vis,
                VersionSummary: $"{ver} ({loader})",
                OccupancyText: occ,
                HostGone: room.HostGone,
                JoinCommand: new AsyncRelayCommand(() => JoinByCodeCoreAsync(code, forceJoin: false))));
        }

        PublicRoomsSummaryText = PublicRooms.Count == 0
            ? "当前没有公开联机房间，你可以创建房间或通过房码加入好友。"
            : $"当前共有 {PublicRooms.Count} 个公开房间可加入。";
        OnPropertyChanged(nameof(HasPublicRooms));
        OnPropertyChanged(nameof(HasNoPublicRooms));
    }

    private async Task RefreshStaleRoomCoreAsync(CancellationToken ct = default)
    {
        var mine = await _api.GetMyLobbyRoomAsync(ct);
        if (mine.Ok && mine.Room is { Status: "open" } room)
        {
            HasStaleRoom = true;
            StaleRoomCode = room.Code;
            StaleRoomSummary = $"检测到你还有一个未关闭的房间「{room.Name}」（房码：{room.Code}），每人同时只能开启 1 个房间。";
        }
        else
        {
            HasStaleRoom = false;
            StaleRoomCode = string.Empty;
            StaleRoomSummary = string.Empty;
        }
    }

    private async Task RefreshInvitesCoreAsync(CancellationToken ct = default)
    {
        var res = await _api.GetLobbyInvitesAsync(ct);
        if (!res.Ok)
            return;

        IncomingInvites.Clear();
        foreach (var inv in res.Incoming)
        {
            var id = inv.Id;
            var roomDisp = string.IsNullOrWhiteSpace(inv.RoomName) ? inv.Code : inv.RoomName!;
            IncomingInvites.Add(new DreamPortLobbyInviteItemVm(
                Id: id,
                Code: inv.Code,
                FromUser: inv.Inviter,
                ToUser: inv.Invitee,
                Message: inv.Message,
                RoomDisplay: roomDisp,
                ExpiresAtMs: inv.ExpiresAtMs,
                IsIncoming: true,
                AcceptCommand: new AsyncRelayCommand(() => AcceptInviteAsync(id)),
                RejectCommand: new AsyncRelayCommand(() => RejectInviteAsync(id)),
                CancelCommand: null));
        }

        OutgoingInvites.Clear();
        foreach (var inv in res.Outgoing)
        {
            var id = inv.Id;
            OutgoingInvites.Add(new DreamPortLobbyInviteItemVm(
                Id: id,
                Code: inv.Code,
                FromUser: inv.Inviter,
                ToUser: inv.Invitee,
                Message: inv.Message,
                RoomDisplay: inv.Code,
                ExpiresAtMs: inv.ExpiresAtMs,
                IsIncoming: false,
                AcceptCommand: null,
                RejectCommand: null,
                CancelCommand: new AsyncRelayCommand(() => CancelInviteAsync(id))));
        }

        NotifyCollectionStates();
    }

    private async Task RefreshFriendsCoreAsync(CancellationToken ct = default)
    {
        var res = await _api.GetFriendsAsync(ct);
        if (!res.Ok)
            return;

        var prev = SelectedFriend;
        FriendOptions.Clear();
        foreach (var f in res.Friends)
            FriendOptions.Add(f);

        if (!string.IsNullOrEmpty(prev) && FriendOptions.Contains(prev))
            SelectedFriend = prev;
        else
            SelectedFriend = FriendOptions.FirstOrDefault();

        OnPropertyChanged(nameof(HasFriendOptions));
    }

    private async Task AcceptInviteAsync(long inviteId)
    {
        if (_tunnel.IsBusy || IsInActiveSession)
        {
            SetStatus("当前已在一个联机房间中，请先离开或结束当前房间。", DreamPortHintLevel.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            var acc = await _api.AcceptLobbyInviteAsync(inviteId);
            if (!acc.Ok || string.IsNullOrWhiteSpace(acc.RoomCode))
            {
                SetStatus(acc.Message ?? "接受邀请失败。", DreamPortHintLevel.Error);
                await RefreshInvitesCoreAsync();
                return;
            }

            await RefreshInvitesCoreAsync();
            IsBusy = false; // JoinByCodeCoreAsync manages IsBusy cleanly
            await JoinByCodeCoreAsync(acc.RoomCode, forceJoin: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RejectInviteAsync(long inviteId)
    {
        IsBusy = true;
        try
        {
            var res = await _api.RejectLobbyInviteAsync(inviteId);
            SetStatus(res.Ok ? $"✓ {res.Message}" : res.Message, res.Ok ? DreamPortHintLevel.Info : DreamPortHintLevel.Error);
            await RefreshInvitesCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CancelInviteAsync(long inviteId)
    {
        IsBusy = true;
        try
        {
            var res = await _api.CancelLobbyInviteAsync(inviteId);
            SetStatus(res.Ok ? $"✓ {res.Message}" : res.Message, res.Ok ? DreamPortHintLevel.Info : DreamPortHintLevel.Error);
            await RefreshInvitesCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StopHostingInternalAsync(bool closeOnServer)
    {
        StopStatsTicker();
        var code = ActiveRoomCode;
        await _tunnel.StopAllAsync().ConfigureAwait(false);
        if (closeOnServer && !string.IsNullOrWhiteSpace(code))
        {
            try
            {
                await _api.CloseLobbyRoomAsync(code).ConfigureAwait(false);
            }
            catch
            {
                // 退出或断网时尽力通知后端关闭
            }
        }

        DispatchUi(() =>
        {
            IsHosting = false;
            ActiveRoomCode = string.Empty;
            ActiveRoomName = string.Empty;
            Members.Clear();
            NotifyCollectionStates();
        });
    }

    private async Task StopGuestInternalAsync()
    {
        StopStatsTicker();
        await _tunnel.StopAllAsync().ConfigureAwait(false);
        DispatchUi(() =>
        {
            IsGuest = false;
            ActiveRoomCode = string.Empty;
            ActiveRoomName = string.Empty;
            GuestLocalAddress = string.Empty;
            IsHostGone = false;
            HostGoneBannerText = string.Empty;
        });
    }

    private void ApplyHostMembers(IReadOnlyList<DreamPortLobbyMember> members)
    {
        Members.Clear();
        var hostSession = _tunnel.CurrentHostSession;
        foreach (var m in members)
        {
            var playerName = m.PlayerName;
            Members.Add(new DreamPortLobbyMemberItemVm(
                PeerId: m.PeerId,
                PlayerName: playerName,
                IsHost: m.IsHost,
                Kicked: m.Kicked,
                JoinedAtMs: m.JoinedAtMs,
                KickCommand: new AsyncRelayCommand(() => hostSession?.KickMemberAsync(playerName) ?? Task.CompletedTask),
                UnkickCommand: new AsyncRelayCommand(() => hostSession?.UnkickMemberAsync(playerName) ?? Task.CompletedTask)));
        }

        var activeCount = members.Count(m => !m.Kicked);
        HostOccupancyText = $"{Math.Max(1, activeCount)} / {(_relayConfig?.MaxPlayersPerRoom is > 0 ? _relayConfig.MaxPlayersPerRoom : 8)} 人";
        OnPropertyChanged(nameof(HasMembers));
    }

    private void HandleHostTunnelEvent(DreamPortLobbyTunnelEvent ev)
    {
        AddEventLog(ev.Message);
        if (ev.Kind is "closed" or "kicked" or "room-closed")
        {
            _ = HandleUnexpectedHostStopAsync(ev.Message);
        }
        else if (ev.Kind == "error")
        {
            SetStatus(ev.Message, DreamPortHintLevel.Warning);
        }
    }

    private async Task HandleUnexpectedHostStopAsync(string reason)
    {
        if (!IsHosting)
            return;
        await StopHostingInternalAsync(closeOnServer: true);
        SetStatus(reason, DreamPortHintLevel.Warning);
    }

    private void HandleGuestTunnelEvent(DreamPortLobbyTunnelEvent ev)
    {
        AddEventLog(ev.Message);
        switch (ev.Kind)
        {
            case "host-gone":
                IsHostGone = true;
                HostGoneBannerText = ev.Message;
                SetStatus(ev.Message, DreamPortHintLevel.Warning);
                break;

            case "host-back":
                IsHostGone = false;
                HostGoneBannerText = string.Empty;
                SetStatus($"✓ {ev.Message}", DreamPortHintLevel.Info);
                break;

            case "kicked":
            case "room-closed":
                _ = HandleGuestTerminatedAsync(ev.Message);
                break;

            case "warn":
                GuestAdvertiseHint = ev.Message;
                break;

            case "error":
                SetStatus(ev.Message, DreamPortHintLevel.Warning);
                break;
        }
    }

    private async Task HandleGuestTerminatedAsync(string reason)
    {
        await StopGuestInternalAsync();
        SetStatus(reason, DreamPortHintLevel.Warning);
        await RefreshPublicRoomsCoreAsync();
    }

    private void StartStatsTicker()
    {
        StopStatsTicker();
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        _statsCts = cts;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    DreamPortLobbyTunnelStats stats = _tunnel.CurrentHostSession?.GetStats()
                        ?? _tunnel.CurrentGuestSession?.GetStats()
                        ?? new DreamPortLobbyTunnelStats(0, 0, 0);
                    DispatchUi(() =>
                        TrafficSummaryText =
                            $"活跃数据连接：{stats.ActiveDataConnections} | 下行：{FormatBytes(stats.BytesIn)} | 上行：{FormatBytes(stats.BytesOut)}");
                }
            }
            catch (OperationCanceledException)
            {
                // normal stop
            }
            finally
            {
                Interlocked.CompareExchange(ref _statsCts, null, cts);
                cts.Dispose();
            }
        });
    }

    private void StopStatsTicker()
    {
        var existing = Interlocked.Exchange(ref _statsCts, null);
        if (existing is null)
            return;
        try { existing.Cancel(); } catch { /* ignore */ }
    }

    private void SyncSessionFromStore()
    {
        var saved = _store.Load();
        if (!string.Equals(_baseUrl, saved.BaseUrl, StringComparison.Ordinal))
        {
            _baseUrl = saved.BaseUrl;
            _api.Dispose();
            _api = _clientFactory(_baseUrl);
        }

        _api.SetToken(saved.Token);
        IsLoggedIn = !string.IsNullOrEmpty(saved.Token);
        CurrentUsername = saved.Username ?? string.Empty;
        AccountSummary = IsLoggedIn
            ? $"站点地址：{saved.BaseUrl} | 当前账号：{CurrentUsername}（{(saved.IsAdmin ? "管理员" : "已登录玩家")}）"
            : $"站点地址：{saved.BaseUrl} | 当前状态：未登录";
    }

    private void HandleSessionExpired(string message)
    {
        _store.ClearToken(_api.BaseUrl);
        _api.SetToken(null);
        IsLoggedIn = false;
        CurrentUsername = string.Empty;
        SetStatus(message, DreamPortHintLevel.Error);
    }

    private void CancelDetectLanPortInternal()
    {
        var existing = Interlocked.Exchange(ref _detectCts, null);
        if (existing is null)
            return;
        try { existing.Cancel(); } catch { /* ignore */ }
    }

    private void AddEventLog(string msg)
    {
        if (string.IsNullOrWhiteSpace(msg))
            return;

        EventLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {msg}");
        while (EventLogs.Count > 20)
            EventLogs.RemoveAt(EventLogs.Count - 1);
        OnPropertyChanged(nameof(HasEventLogs));
    }

    private void NotifyCollectionStates()
    {
        OnPropertyChanged(nameof(HasPublicRooms));
        OnPropertyChanged(nameof(HasNoPublicRooms));
        OnPropertyChanged(nameof(HasMembers));
        OnPropertyChanged(nameof(HasIncomingInvites));
        OnPropertyChanged(nameof(HasOutgoingInvites));
        OnPropertyChanged(nameof(HasFriendOptions));
        OnPropertyChanged(nameof(HasEventLogs));
    }

    private void DispatchUi(Action action)
    {
        if (_uiCtx is not null && !ReferenceEquals(SynchronizationContext.Current, _uiCtx))
            _uiCtx.Post(_ => action(), null);
        else
            action();
    }

    private void SetStatus(string message, DreamPortHintLevel level)
    {
        StatusHint = message;
        HintLevel = level;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024d:F1} KB";
        return $"{bytes / (1024d * 1024d):F2} MB";
    }
}
