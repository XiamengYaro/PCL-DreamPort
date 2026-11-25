using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamPort.Core.Api;
using DreamPort.Core.Auth;
using DreamPort.Core.Config;
using DreamPort.UI.Services;
using PCL.Core.App;

namespace DreamPort.UI.ViewModels;

/// <summary>
/// DreamPort 公告与站内通知中心视图模型：
/// - 服务器公告（<c>/api/announcements</c>）公开可读，无需登录；
/// - 站内通知（<c>/api/notifications</c>）感知当前登录会话，支持未读筛选、单条已读、全部已读与安全链接跳转，并同步更新标题栏未读角标。
/// </summary>
public partial class DreamPortCommunityViewModel : ObservableObject, IDisposable
{
    private readonly DreamPortSessionStore _store;
    private readonly Func<string, DreamPortApiClient> _clientFactory;
    private readonly Action<string> _openUrl;
    private readonly Action? _requestGoToAccount;
    private readonly List<DreamPortNotificationItemVm> _allNotifications = [];
    private DreamPortApiClient _api;
    private string _baseUrl = DreamPortBuildConfig.BaseUrl;
    private bool _loadedOnce;

    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private int _unreadCount;
    [ObservableProperty] private bool _showUnreadOnly;
    [ObservableProperty] private bool _isNotificationsLoading;
    [ObservableProperty] private bool _isAnnouncementsLoading;
    [ObservableProperty] private string _notificationsSummaryText = "登录后可同步查看站内通知、审核提醒与申诉进度。";
    [ObservableProperty] private string _announcementsSummaryText = "展示来自梦港站点的服务器公告与版本更新资讯。";
    [ObservableProperty] private string _statusMessage = "正在准备加载服务器公告与站内通知...";
    [ObservableProperty] private DreamPortHintLevel _hintLevel = DreamPortHintLevel.Info;
    [ObservableProperty] private bool _isStatusError;

    public ObservableCollection<DreamPortNotificationItemVm> VisibleNotifications { get; } = [];
    public ObservableCollection<DreamPortAnnouncementItemVm> Announcements { get; } = [];

    public DreamPortCommunityViewModel(
        DreamPortSessionStore? store = null,
        Func<string, DreamPortApiClient>? clientFactory = null,
        Action<string>? openUrl = null,
        Action? requestGoToAccount = null)
    {
        _store = store ?? new DreamPortSessionStore();
        _clientFactory = clientFactory ?? (url => new DreamPortApiClient(url));
        _openUrl = openUrl ?? (url => Basics.OpenPath(url));
        _requestGoToAccount = requestGoToAccount;

        var saved = _store.Load();
        _baseUrl = saved.BaseUrl;
        _isLoggedIn = !string.IsNullOrEmpty(saved.Token);
        _api = _clientFactory(_baseUrl);
        if (!string.IsNullOrEmpty(saved.Token))
            _api.SetToken(saved.Token);
    }

    public bool IsNotLoggedIn => !IsLoggedIn;
    public bool IsBusy => IsNotificationsLoading || IsAnnouncementsLoading;
    public bool IsNotBusy => !IsBusy;
    public bool HasNotifications => VisibleNotifications.Count > 0;
    public bool HasNoNotifications => VisibleNotifications.Count == 0;
    public bool HasAnnouncements => Announcements.Count > 0;
    public bool HasNoAnnouncements => Announcements.Count == 0;
    public bool CanMarkAllRead => IsLoggedIn && UnreadCount > 0 && IsNotBusy;

    public bool? ShowUnreadOnlyChecked
    {
        get => ShowUnreadOnly;
        set
        {
            var next = value == true;
            if (ShowUnreadOnly != next)
                ShowUnreadOnly = next;
        }
    }

    partial void OnIsLoggedInChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotLoggedIn));
        OnPropertyChanged(nameof(CanMarkAllRead));
    }

    partial void OnUnreadCountChanged(int value)
    {
        OnPropertyChanged(nameof(CanMarkAllRead));
    }

    partial void OnIsNotificationsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsNotBusy));
        OnPropertyChanged(nameof(CanMarkAllRead));
    }

    partial void OnIsAnnouncementsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsNotBusy));
        OnPropertyChanged(nameof(CanMarkAllRead));
    }

    partial void OnShowUnreadOnlyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowUnreadOnlyChecked));
        RebuildVisibleNotifications();
    }

    public async Task InitializeOrSyncAsync(CancellationToken ct = default)
    {
        var saved = _store.Load();
        var siteChanged = !string.Equals(_baseUrl, saved.BaseUrl, StringComparison.Ordinal);
        var prevToken = _api.CurrentToken;
        var nextToken = saved.Token;

        if (siteChanged)
        {
            _baseUrl = saved.BaseUrl;
            _api.Dispose();
            _api = _clientFactory(_baseUrl);
        }

        _api.SetToken(nextToken);
        IsLoggedIn = !string.IsNullOrEmpty(nextToken);

        var sessionChanged = siteChanged || !string.Equals(prevToken, nextToken, StringComparison.Ordinal);
        if (_loadedOnce && !sessionChanged)
            return;

        _loadedOnce = true;
        await RefreshAllAsync(ct);
    }

    [RelayCommand]
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        SyncSessionFromStore();
        await Task.WhenAll(RefreshAnnouncementsAsync(ct), RefreshNotificationsAsync(ct));
    }

    [RelayCommand]
    private async Task RefreshAnnouncementsAsync(CancellationToken ct = default)
    {
        SyncBaseUrlFromStore();
        IsAnnouncementsLoading = true;
        try
        {
            var res = await _api.GetAnnouncementsAsync(ct);
            Announcements.Clear();
            if (!res.Ok)
            {
                AnnouncementsSummaryText = res.Message ?? "加载服务器公告失败。";
                OnPropertyChanged(nameof(HasAnnouncements));
                OnPropertyChanged(nameof(HasNoAnnouncements));
                SetStatus(AnnouncementsSummaryText, DreamPortHintLevel.Warning);
                return;
            }

            foreach (var item in res.Items)
            {
                Announcements.Add(new DreamPortAnnouncementItemVm(
                    Title: item.Title,
                    Body: item.Body,
                    DateText: item.DateText,
                    Tag: item.Tag,
                    Important: item.Important));
            }

            OnPropertyChanged(nameof(HasAnnouncements));
            OnPropertyChanged(nameof(HasNoAnnouncements));
            AnnouncementsSummaryText = Announcements.Count == 0
                ? "当前暂无已发布的服务器公告。"
                : $"已加载 {Announcements.Count} 条服务器公告。";
            SetStatus($"✓ 已同步最新公告，共 {Announcements.Count} 条。", DreamPortHintLevel.Info);
        }
        finally
        {
            IsAnnouncementsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RefreshNotificationsAsync(CancellationToken ct = default)
    {
        SyncSessionFromStore();
        if (!IsLoggedIn)
        {
            ClearNotificationsState("未登录梦港账号，仅可浏览下方公开服务器公告；登录后可同步站内通知与未读角标。");
            DreamPortShellBridge.ResetUnreadCount();
            return;
        }

        IsNotificationsLoading = true;
        try
        {
            var res = await _api.GetNotificationsAsync(page: 1, size: 30, ct: ct);
            if (!res.Ok)
            {
                if (res.FailureReason == DreamPortFailureReason.Expired)
                {
                    _store.ClearToken(_api.BaseUrl);
                    _api.SetToken(null);
                    IsLoggedIn = false;
                    ClearNotificationsState("登录会话已过期，请前往「梦港账号」重新授权登录。");
                    DreamPortShellBridge.ResetUnreadCount();
                    SetStatus(res.Message ?? "登录会话已过期，请重新登录。", DreamPortHintLevel.Warning);
                    return;
                }

                NotificationsSummaryText = res.Message ?? "获取站内通知失败。";
                SetStatus(NotificationsSummaryText, DreamPortHintLevel.Warning);
                return;
            }

            _allNotifications.Clear();
            foreach (var item in res.Items)
            {
                DreamPortNotificationItemVm? rowVm = null;
                rowVm = new DreamPortNotificationItemVm(
                    id: item.Id,
                    type: item.Type,
                    title: item.Title,
                    body: item.Body,
                    link: item.Link,
                    read: item.Read,
                    createdAtMs: item.CreatedAtMs,
                    hasSafeLink: DreamPortApiClient.TryResolveSafeWebUrl(_baseUrl, item.Link, out _),
                    markReadCommand: new AsyncRelayCommand(() => MarkNotificationReadAsync(rowVm!)),
                    openLinkCommand: new AsyncRelayCommand(() => OpenNotificationLinkAsync(rowVm!)));
                _allNotifications.Add(rowVm);
            }

            UnreadCount = Math.Max(0, res.Unread);
            DreamPortShellBridge.PublishUnreadCount(UnreadCount);
            RebuildVisibleNotifications();
            NotificationsSummaryText = _allNotifications.Count == 0
                ? "当前没有站内通知。"
                : $"共 {res.Total} 条通知，其中未读 {UnreadCount} 条。";
        }
        finally
        {
            IsNotificationsLoading = false;
        }
    }

    [RelayCommand]
    private async Task MarkAllReadAsync()
    {
        if (!IsLoggedIn)
        {
            SetStatus("请先登录梦港账号。", DreamPortHintLevel.Warning);
            return;
        }

        IsNotificationsLoading = true;
        try
        {
            var res = await _api.MarkAllNotificationsReadAsync();
            if (!res.Ok)
            {
                if (res.FailureReason == DreamPortFailureReason.Expired)
                {
                    _store.ClearToken(_api.BaseUrl);
                    _api.SetToken(null);
                    IsLoggedIn = false;
                    ClearNotificationsState("登录会话已过期，请重新登录。");
                    DreamPortShellBridge.ResetUnreadCount();
                }

                SetStatus(res.Message ?? "全部标记已读失败。", DreamPortHintLevel.Error);
                return;
            }

            foreach (var item in _allNotifications)
                item.Read = true;

            UnreadCount = Math.Max(0, res.Unread);
            DreamPortShellBridge.PublishUnreadCount(UnreadCount);
            RebuildVisibleNotifications();
            NotificationsSummaryText = _allNotifications.Count == 0
                ? "当前没有站内通知。"
                : $"共 {_allNotifications.Count} 条通知，全部已读。";
            SetStatus($"✓ {res.Message ?? "已全部标记已读"}", DreamPortHintLevel.Info);
        }
        finally
        {
            IsNotificationsLoading = false;
        }
    }

    [RelayCommand]
    private void GoToAccountPage()
    {
        _requestGoToAccount?.Invoke();
    }

    [RelayCommand]
    private void OpenAnnouncementsWebPage() => OpenSitePath("/announcements", "官网公告中心");

    [RelayCommand]
    private void OpenNotificationsWebPage() => OpenSitePath("/notifications", "官网通知中心");

    public void Dispose()
    {
        _api.Dispose();
    }

    private async Task MarkNotificationReadAsync(DreamPortNotificationItemVm item)
    {
        if (!IsLoggedIn || item.Read)
            return;

        var res = await _api.MarkNotificationReadAsync(item.Id);
        if (!res.Ok)
        {
            if (res.FailureReason == DreamPortFailureReason.Expired)
            {
                _store.ClearToken(_api.BaseUrl);
                _api.SetToken(null);
                IsLoggedIn = false;
                ClearNotificationsState("登录会话已过期，请重新登录。");
                DreamPortShellBridge.ResetUnreadCount();
            }

            SetStatus(res.Message ?? "标记通知已读失败。", DreamPortHintLevel.Error);
            return;
        }

        item.Read = true;
        UnreadCount = Math.Max(0, res.Unread);
        DreamPortShellBridge.PublishUnreadCount(UnreadCount);
        RebuildVisibleNotifications();
        NotificationsSummaryText = _allNotifications.Count == 0
            ? "当前没有站内通知。"
            : $"已加载 {_allNotifications.Count} 条通知，其中未读 {UnreadCount} 条。";
    }

    private async Task OpenNotificationLinkAsync(DreamPortNotificationItemVm item)
    {
        if (!DreamPortApiClient.TryResolveSafeWebUrl(_baseUrl, item.Link, out var url))
        {
            SetStatus("该通知的跳转链接格式不受支持，已拦截打开。", DreamPortHintLevel.Warning);
            return;
        }

        if (!item.Read)
            await MarkNotificationReadAsync(item);

        try
        {
            _openUrl(url);
            SetStatus($"已在浏览器打开通知关联页面。地址：{url}", DreamPortHintLevel.Info);
        }
        catch
        {
            SetStatus($"无法自动打开浏览器，请手动访问：{url}", DreamPortHintLevel.Warning);
        }
    }

    private void RebuildVisibleNotifications()
    {
        VisibleNotifications.Clear();
        IEnumerable<DreamPortNotificationItemVm> seq = _allNotifications;
        if (ShowUnreadOnly)
            seq = seq.Where(i => i.IsUnread);
        foreach (var item in seq)
            VisibleNotifications.Add(item);

        OnPropertyChanged(nameof(HasNotifications));
        OnPropertyChanged(nameof(HasNoNotifications));
    }

    private void ClearNotificationsState(string summary)
    {
        _allNotifications.Clear();
        VisibleNotifications.Clear();
        UnreadCount = 0;
        NotificationsSummaryText = summary;
        OnPropertyChanged(nameof(HasNotifications));
        OnPropertyChanged(nameof(HasNoNotifications));
    }

    private void SyncBaseUrlFromStore()
    {
        var saved = _store.Load();
        if (!string.Equals(_baseUrl, saved.BaseUrl, StringComparison.Ordinal))
        {
            _baseUrl = saved.BaseUrl;
            var token = _api.CurrentToken;
            _api.Dispose();
            _api = _clientFactory(_baseUrl);
            _api.SetToken(token);
        }
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
    }

    private void OpenSitePath(string path, string label)
    {
        if (!DreamPortApiClient.TryResolveSafeWebUrl(_baseUrl, path, out var url))
        {
            SetStatus("当前站点地址无效，请先在「梦港账号」核对服务地址。", DreamPortHintLevel.Error);
            return;
        }

        try
        {
            _openUrl(url);
            SetStatus($"已在浏览器打开{label}。地址：{url}", DreamPortHintLevel.Info);
        }
        catch
        {
            SetStatus($"无法自动打开浏览器，请手动访问：{url}", DreamPortHintLevel.Warning);
        }
    }

    private void SetStatus(string message, DreamPortHintLevel level)
    {
        StatusMessage = message;
        HintLevel = level;
        IsStatusError = level == DreamPortHintLevel.Error;
    }
}
