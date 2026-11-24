using System.Text;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamPort.Core.Api;
using DreamPort.Core.Auth;
using DreamPort.Core.Config;
using DreamPort.UI.Helpers;
using PCL.Core.App;

namespace DreamPort.UI.ViewModels;

/// <summary>
/// DreamPort 账号登录（浏览器确认 + 同码二维码兼容）与主页模式切换视图模型。
/// </summary>
public partial class DreamPortAccountViewModel : ObservableObject, IDisposable
{
    private readonly DreamPortSessionStore _store;
    private readonly Action? _onHomepagePreferenceChanged;
    private readonly Action<string> _openUrl;
    private readonly Func<string, DreamPortApiClient> _clientFactory;
    private DreamPortApiClient _api;
    private CancellationTokenSource? _waitCts;
    private bool _initialized;

    [ObservableProperty] private string _baseUrl = DreamPortBuildConfig.BaseUrl;
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isWaitingBrowserConfirm;
    [ObservableProperty] private string _confirmUrl = string.Empty;
    [ObservableProperty] private Bitmap? _qrCodeBitmap;
    [ObservableProperty] private string _countdownText = string.Empty;
    [ObservableProperty] private string _statusMessage = "未登录 DreamPort 站点账号。";
    [ObservableProperty] private bool _isStatusError;
    [ObservableProperty] private string _loggedInUsername = string.Empty;
    [ObservableProperty] private string _accountMetaSummary = string.Empty;
    [ObservableProperty] private int _homepageModeIndex;

    public DreamPortAccountViewModel(
        Action? onHomepagePreferenceChanged = null,
        DreamPortSessionStore? store = null,
        Func<string, DreamPortApiClient>? clientFactory = null,
        Action<string>? openUrl = null)
    {
        _onHomepagePreferenceChanged = onHomepagePreferenceChanged;
        _store = store ?? new DreamPortSessionStore();
        _clientFactory = clientFactory ?? (url => new DreamPortApiClient(url));
        _openUrl = openUrl ?? (url => Basics.OpenPath(url));

        var saved = _store.Load();
        _baseUrl = saved.BaseUrl;
        _loggedInUsername = saved.Username ?? string.Empty;
        _isLoggedIn = !string.IsNullOrEmpty(saved.Token);
        _homepageModeIndex = Math.Clamp(SafeGetDreamPortHomepageMode(), 0, 2);

        _api = _clientFactory(_baseUrl);
        if (!string.IsNullOrEmpty(saved.Token))
            _api.SetToken(saved.Token);

        UpdateMetaSummary(saved.IsAdmin, saved.IsTokenPersistent, profile: null);
    }

    public bool IsNotLoggedIn => !IsLoggedIn;

    partial void OnIsLoggedInChanged(bool value) => OnPropertyChanged(nameof(IsNotLoggedIn));

    public async Task InitializeAsync()
    {
        if (_initialized)
            return;
        _initialized = true;

        var saved = _store.Load();
        if (!string.IsNullOrEmpty(saved.Token))
        {
            await ValidateAndRefreshProfileAsync();
            return;
        }

        if (!string.IsNullOrEmpty(saved.Username) && !saved.IsTokenPersistent)
        {
            SetStatus("上次登录凭据未持久保存（系统加密存储不可用），请通过浏览器重新授权登录。", isError: false);
        }

        await ProbeConfigAsync();
    }

    [RelayCommand]
    private async Task CheckServerAsync()
    {
        if (!EnsureClientForBaseUrl())
            return;

        _store.SaveBaseUrlOnly(_api.BaseUrl);
        await ProbeConfigAsync();
    }

    [RelayCommand]
    private async Task StartBrowserLoginAsync()
    {
        if (!EnsureClientForBaseUrl())
            return;

        CancelActiveLoginWait();
        IsBusy = true;
        SetStatus("正在创建 DreamPort 授权会话...", isError: false);

        (DreamPortBrowserLoginSession? session, string? error) createResult;
        try
        {
            createResult = await _api.CreateBrowserLoginAsync();
        }
        finally
        {
            IsBusy = false;
        }

        if (createResult.session is null)
        {
            SetStatus(createResult.error ?? "发起浏览器授权登录失败。", isError: true);
            return;
        }

        var session = createResult.session;
        ConfirmUrl = session.ConfirmUrl;
        QrCodeBitmap = QrCodeRenderer.RenderToBitmap(session.ConfirmUrl, 6);
        IsWaitingBrowserConfirm = true;
        UpdateCountdown(session.ExpiresAtMs);

        var browserOpened = TryOpenBrowser(session.ConfirmUrl);
        SetStatus(
            browserOpened
                ? "已打开系统浏览器授权页，请在浏览器中登录并点击「确认登录」（也可使用已登录的手机扫码）。"
                : "未能自动打开浏览器，请复制下方授权链接到浏览器中打开，或用手机扫码确认。",
            isError: false);

        var cts = new CancellationTokenSource();
        _waitCts = cts;
        _ = RunBrowserLoginWaitLoopAsync(session, cts);
    }

    [RelayCommand]
    private void ReopenBrowser()
    {
        if (string.IsNullOrWhiteSpace(ConfirmUrl))
            return;

        var ok = TryOpenBrowser(ConfirmUrl);
        SetStatus(
            ok ? "已重新在系统浏览器中打开授权页面，请完成确认。" : "拉起浏览器失败，请手动复制下方链接打开。",
            isError: !ok);
    }

    [RelayCommand]
    private void CancelBrowserLogin()
    {
        CancelActiveLoginWait();
        ClearWaitingState();
        SetStatus("已取消本次浏览器授权登录。", isError: false);
    }

    [RelayCommand]
    private async Task ValidateAndRefreshProfileAsync()
    {
        if (string.IsNullOrEmpty(_api.CurrentToken))
        {
            SetStatus("当前未登录。", isError: true);
            return;
        }

        IsBusy = true;
        try
        {
            var valid = await _api.ValidateSessionAsync();
            if (!valid.Valid)
            {
                if (valid.Reason == DreamPortFailureReason.Network)
                {
                    SetStatus($"网络不可达：{valid.Message ?? "请检查网络"}（已保留本地登录状态）", isError: true);
                    return;
                }

                _api.SetToken(null);
                _store.ClearToken(_api.BaseUrl);
                IsLoggedIn = false;
                LoggedInUsername = string.Empty;
                AccountMetaSummary = string.Empty;
                SetStatus(valid.Message ?? "登录已过期，请通过浏览器重新授权登录。", isError: true);
                return;
            }

            var saved = _store.Save(_api.BaseUrl, valid.Username ?? LoggedInUsername, valid.IsAdmin, _api.CurrentToken);
            IsLoggedIn = true;
            LoggedInUsername = saved.Username ?? string.Empty;

            var profile = await _api.GetProfileAsync();
            UpdateMetaSummary(saved.IsAdmin, saved.IsTokenPersistent, profile);
            SetStatus("✓ 已连接 DreamPort 站点并完成会话校验。", isError: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        CancelActiveLoginWait();
        ClearWaitingState();
        IsBusy = true;
        try
        {
            await _api.LogoutAsync();
            _store.ClearToken(_api.BaseUrl);
            IsLoggedIn = false;
            LoggedInUsername = string.Empty;
            AccountMetaSummary = string.Empty;
            SetStatus("已退出登录并吊销当前设备会话。", isError: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ApplyHomepageMode()
    {
        var clamped = Math.Clamp(HomepageModeIndex, 0, 2);
        HomepageModeIndex = clamped;
        SafeSetDreamPortHomepageMode(clamped);
        _onHomepagePreferenceChanged?.Invoke();

        var label = clamped switch
        {
            0 => "远端优先并静默回退本地原型",
            1 => "仅使用本地内置原型",
            _ => "空白页"
        };
        SetStatus($"✓ 主页模式已切换为「{label}」，并已触发主页刷新。", isError: false);
    }

    public void Dispose()
    {
        CancelActiveLoginWait();
        _api.Dispose();
    }

    private async Task RunBrowserLoginWaitLoopAsync(DreamPortBrowserLoginSession session, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        using var tickerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tickerTask = RunCountdownTickerAsync(session.ExpiresAtMs, tickerCts.Token);

        try
        {
            await WaitForApprovalLoopAsync(session, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户主动取消或发起新一轮授权
        }
        finally
        {
            tickerCts.Cancel();
            try
            {
                await tickerTask;
            }
            catch (OperationCanceledException)
            {
                // expected
            }

            if (ReferenceEquals(_waitCts, cts))
            {
                _waitCts.Dispose();
                _waitCts = null;
            }
        }
    }

    private async Task WaitForApprovalLoopAsync(DreamPortBrowserLoginSession session, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= session.ExpiresAtMs)
            {
                ClearWaitingState();
                SetStatus("登录授权已超时（5 分钟有效），请重新点击「在浏览器中登录」。", isError: true);
                return;
            }

            var poll = await _api.PollBrowserLoginAsync(session.QrToken, ct);
            switch (poll.Status)
            {
                case DreamPortBrowserLoginStatus.Approved:
                {
                    var consumed = await _api.ConsumeBrowserLoginAsync(session.QrToken, ct);
                    if (consumed.Ok)
                    {
                        ClearWaitingState();
                        var saved = _store.Save(_api.BaseUrl, consumed.Username, consumed.IsAdmin, consumed.Token);
                        IsLoggedIn = true;
                        LoggedInUsername = saved.Username ?? string.Empty;

                        var profile = await _api.GetProfileAsync(ct);
                        UpdateMetaSummary(saved.IsAdmin, saved.IsTokenPersistent, profile);
                        var suffix = saved.IsTokenPersistent ? "凭据已加密保存。" : "系统加密存储不可用，本次令牌仅保留在内存中。";
                        SetStatus($"✓ 授权登录成功，欢迎 {LoggedInUsername}！{suffix}", isError: false);
                        return;
                    }

                    if (consumed.Status == DreamPortBrowserLoginStatus.NetworkError)
                    {
                        SetStatus("已确认授权，获取令牌时遇到网络波动，正在自动重试...", isError: false);
                        await Task.Delay(TimeSpan.FromSeconds(2), ct);
                        continue;
                    }

                    ClearWaitingState();
                    SetStatus(consumed.Message ?? "授权凭据已失效，请重新登录。", isError: true);
                    return;
                }
                case DreamPortBrowserLoginStatus.Denied:
                    ClearWaitingState();
                    SetStatus("已在确认端拒绝此次登录请求。", isError: true);
                    return;

                case DreamPortBrowserLoginStatus.Expired:
                case DreamPortBrowserLoginStatus.NotFound:
                    ClearWaitingState();
                    SetStatus(poll.Message ?? "本次登录授权已过期，请重新点击「在浏览器中登录」。", isError: true);
                    return;

                case DreamPortBrowserLoginStatus.Consumed:
                    ClearWaitingState();
                    SetStatus("该登录授权凭据已被使用，请重新发起。", isError: true);
                    return;

                case DreamPortBrowserLoginStatus.NetworkError:
                    SetStatus("连接站点遇到短暂波动，仍在等待浏览器确认...", isError: false);
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;

                default:
                {
                    var waitStarted = DateTimeOffset.UtcNow;
                    await _api.WaitBrowserLoginChangeAsync(session.QrToken, TimeSpan.FromSeconds(10), ct);
                    var elapsed = DateTimeOffset.UtcNow - waitStarted;
                    if (elapsed < TimeSpan.FromMilliseconds(1500))
                        await Task.Delay(TimeSpan.FromMilliseconds(1500) - elapsed, ct);
                    continue;
                }
            }
        }
    }

    private async Task RunCountdownTickerAsync(long expiresAtMs, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct))
        {
            UpdateCountdown(expiresAtMs);
        }
    }

    private void UpdateCountdown(long expiresAtMs)
    {
        var remainingMs = expiresAtMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (remainingMs <= 0)
        {
            CountdownText = "剩余 00:00（即将超时）";
            return;
        }

        var remaining = TimeSpan.FromMilliseconds(remainingMs);
        CountdownText = $"剩余 {(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}";
    }

    private async Task ProbeConfigAsync()
    {
        IsBusy = true;
        try
        {
            var cfg = await _api.GetConfigAsync();
            if (cfg is null)
            {
                SetStatus($"未能获取站点配置（{_api.BaseUrl}），仅可离线使用基础功能。", isError: false);
                return;
            }

            var name = string.IsNullOrWhiteSpace(cfg.SiteName) ? _api.BaseUrl : cfg.SiteName;
            SetStatus($"✓ 已连通站点：{name}", isError: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool TryOpenBrowser(string url)
    {
        try
        {
            _openUrl(url);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void CancelActiveLoginWait()
    {
        var existing = _waitCts;
        _waitCts = null;
        if (existing is null)
            return;

        try
        {
            existing.Cancel();
        }
        catch
        {
            // ignore
        }
        finally
        {
            existing.Dispose();
        }
    }

    private void ClearWaitingState()
    {
        IsWaitingBrowserConfirm = false;
        ConfirmUrl = string.Empty;
        QrCodeBitmap = null;
        CountdownText = string.Empty;
    }

    private bool EnsureClientForBaseUrl()
    {
        if (!DreamPortApiClient.TryNormalizeBaseUrl(BaseUrl, out var normalized, out var err))
        {
            SetStatus(err ?? "站点地址格式无效。", isError: true);
            return false;
        }

        BaseUrl = normalized;
        if (!string.Equals(_api.BaseUrl, normalized, StringComparison.Ordinal))
        {
            var oldToken = _api.CurrentToken;
            _api.Dispose();
            _api = _clientFactory(normalized);
            if (!string.IsNullOrEmpty(oldToken))
                _api.SetToken(oldToken);
        }

        return true;
    }

    private void UpdateMetaSummary(bool isAdmin, bool isTokenPersistent, DreamPortProfileResult? profile)
    {
        var sb = new StringBuilder();
        sb.Append($"服务地址：{_api.BaseUrl}");
        sb.Append($" | 身份：{(isAdmin ? "管理员" : "普通玩家")}");
        sb.Append($" | 会话存储：{(isTokenPersistent ? "系统安全加密持久化" : "仅当前进程内存")}");

        if (profile is not null)
        {
            if (!string.IsNullOrWhiteSpace(profile.MinecraftName))
                sb.Append($"\n绑定正版角色：{profile.MinecraftName}");
            if (!string.IsNullOrWhiteSpace(profile.Status))
                sb.Append($" | 账号状态：{profile.Status}");
            if (profile.Points.HasValue || profile.CheckinStreak.HasValue)
                sb.Append($"\n积分：{profile.Points?.ToString() ?? "-"} | 连续签到：{profile.CheckinStreak?.ToString() ?? "-"} 天");
        }

        AccountMetaSummary = sb.ToString();
    }

    private void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        IsStatusError = isError;
    }

    private static int SafeGetDreamPortHomepageMode()
    {
        try
        {
            return Config.Preference.Homepage.DreamPortMode;
        }
        catch
        {
            return 0;
        }
    }

    private static void SafeSetDreamPortHomepageMode(int mode)
    {
        try
        {
            Config.Preference.Homepage.Type = 0;
            Config.Preference.Homepage.DreamPortMode = mode;
        }
        catch
        {
            // 测试宿主未初始化 Config 时忽略落盘
        }
    }
}
