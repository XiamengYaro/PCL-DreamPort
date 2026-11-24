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
/// DreamPort 账号登录与主页模式切换视图模型（Phase 1 PoC）。
/// </summary>
public partial class DreamPortAccountViewModel : ObservableObject, IDisposable
{
    private readonly DreamPortSessionStore _store;
    private readonly Action? _onHomepagePreferenceChanged;
    private readonly Func<string, DreamPortApiClient> _clientFactory;
    private DreamPortApiClient _api;
    private string? _captchaToken;
    private string? _challengeId;
    private bool _initialized;

    [ObservableProperty] private string _baseUrl = DreamPortBuildConfig.BaseUrl;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _isCaptchaRequired;
    [ObservableProperty] private string _captchaInput = string.Empty;
    [ObservableProperty] private Bitmap? _captchaImage;
    [ObservableProperty] private bool _needs2Fa;
    [ObservableProperty] private string _twoFactorCode = string.Empty;
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isBanned;
    [ObservableProperty] private string _banSummary = string.Empty;
    [ObservableProperty] private string _statusMessage = "未登录 DreamPort 站点账号。";
    [ObservableProperty] private bool _isStatusError;
    [ObservableProperty] private string _loggedInUsername = string.Empty;
    [ObservableProperty] private string _accountMetaSummary = string.Empty;
    [ObservableProperty] private int _homepageModeIndex;

    public DreamPortAccountViewModel(
        Action? onHomepagePreferenceChanged = null,
        DreamPortSessionStore? store = null,
        Func<string, DreamPortApiClient>? clientFactory = null)
    {
        _onHomepagePreferenceChanged = onHomepagePreferenceChanged;
        _store = store ?? new DreamPortSessionStore();
        _clientFactory = clientFactory ?? (url => new DreamPortApiClient(url));

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
            SetStatus("上次登录凭据未持久保存（系统加密存储不可用），请重新输入密码登录。", isError: false);
        }

        await ProbeConfigAndCaptchaAsync();
    }

    [RelayCommand]
    private async Task CheckServerAsync()
    {
        if (!EnsureClientForBaseUrl())
            return;

        _store.SaveBaseUrlOnly(_api.BaseUrl);
        await ProbeConfigAndCaptchaAsync();
    }

    [RelayCommand]
    private async Task RefreshCaptchaAsync()
    {
        if (!EnsureClientForBaseUrl())
            return;

        IsBusy = true;
        try
        {
            var captcha = await _api.GenerateCaptchaAsync();
            if (captcha is null)
            {
                SetStatus("获取图形验证码失败，请检查站点地址与网络。", isError: true);
                return;
            }

            _captchaToken = captcha.CaptchaToken;
            CaptchaInput = string.Empty;
            var bmp = ImageDataUriHelper.TryDecodeRasterImage(captcha.ImageDataUri);
            if (bmp is null)
            {
                CaptchaImage = null;
                SetStatus("站点返回了暂不支持的验证码格式（如 SVG），请确认服务端支持 PNG Base64。", isError: true);
                return;
            }

            CaptchaImage = bmp;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (!EnsureClientForBaseUrl())
            return;

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password))
        {
            SetStatus("请输入账号与密码。", isError: true);
            return;
        }

        if (IsCaptchaRequired && (string.IsNullOrWhiteSpace(_captchaToken) || string.IsNullOrWhiteSpace(CaptchaInput)))
        {
            SetStatus("请输入图形验证码。", isError: true);
            return;
        }

        IsBusy = true;
        IsBanned = false;
        BanSummary = string.Empty;
        SetStatus("正在登录 DreamPort...", isError: false);

        try
        {
            var res = await _api.LoginAsync(
                Username.Trim(),
                Password,
                IsCaptchaRequired ? _captchaToken : null,
                IsCaptchaRequired ? CaptchaInput : null);

            await HandleLoginResultAsync(res);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SubmitTotpAsync()
    {
        if (string.IsNullOrEmpty(_challengeId))
        {
            SetStatus("二次验证会话已失效，请重新登录。", isError: true);
            Needs2Fa = false;
            return;
        }

        if (string.IsNullOrWhiteSpace(TwoFactorCode))
        {
            SetStatus("请输入 6 位动态验证码。", isError: true);
            return;
        }

        IsBusy = true;
        try
        {
            var res = await _api.SubmitTotpAsync(_challengeId, TwoFactorCode);
            await HandleLoginResultAsync(res);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel2Fa()
    {
        Needs2Fa = false;
        _challengeId = null;
        TwoFactorCode = string.Empty;
        SetStatus("已取消二次验证。", isError: false);
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
                SetStatus(valid.Message ?? "登录已过期，请重新登录。", isError: true);
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
        IsBusy = true;
        try
        {
            await _api.LogoutAsync();
            _store.ClearToken(_api.BaseUrl);
            IsLoggedIn = false;
            Needs2Fa = false;
            IsBanned = false;
            BanSummary = string.Empty;
            LoggedInUsername = string.Empty;
            AccountMetaSummary = string.Empty;
            Password = string.Empty;
            TwoFactorCode = string.Empty;
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
        _api.Dispose();
    }

    private async Task ProbeConfigAndCaptchaAsync()
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

            IsCaptchaRequired = cfg.LoginCaptchaRequired;
            var name = string.IsNullOrWhiteSpace(cfg.SiteName) ? _api.BaseUrl : cfg.SiteName;
            SetStatus($"✓ 已连通站点：{name}", isError: false);
            if (IsCaptchaRequired)
                await RefreshCaptchaAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task HandleLoginResultAsync(DreamPortLoginResult res)
    {
        switch (res.Outcome)
        {
            case DreamPortLoginOutcome.Ok:
            {
                Password = string.Empty;
                TwoFactorCode = string.Empty;
                CaptchaInput = string.Empty;
                Needs2Fa = false;
                _challengeId = null;

                var saved = _store.Save(_api.BaseUrl, res.Username, res.IsAdmin, res.Token);
                IsLoggedIn = true;
                LoggedInUsername = saved.Username ?? Username.Trim();
                var profile = await _api.GetProfileAsync();
                UpdateMetaSummary(saved.IsAdmin, saved.IsTokenPersistent, profile);

                var suffix = saved.IsTokenPersistent ? "凭据已加密保存。" : "系统加密存储不可用，本次令牌仅保留在内存中。";
                SetStatus($"✓ 登录成功，欢迎 {LoggedInUsername}！{suffix}", isError: false);
                return;
            }
            case DreamPortLoginOutcome.Need2Fa:
                Needs2Fa = true;
                _challengeId = res.ChallengeId;
                TwoFactorCode = string.Empty;
                SetStatus("此账号已开启两步验证，请输入 TOTP 动态验证码。", isError: false);
                return;

            case DreamPortLoginOutcome.Need2FaSetup:
                Needs2Fa = false;
                SetStatus(res.ErrorMessage ?? "需要前往网页端完成 2FA 绑定。", isError: true);
                return;

            case DreamPortLoginOutcome.NeedCaptcha:
                IsCaptchaRequired = true;
                SetStatus(string.IsNullOrWhiteSpace(res.ErrorMessage) ? "请先完成图形验证码。" : res.ErrorMessage, isError: true);
                await RefreshCaptchaAsync();
                return;

            case DreamPortLoginOutcome.Banned:
                Needs2Fa = false;
                IsBanned = true;
                BanSummary = FormatBanSummary(res);
                SetStatus("✗ 该账号已被封禁，无法登录。", isError: true);
                return;

            default:
                SetStatus(res.ErrorMessage ?? "登录失败。", isError: true);
                return;
        }
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

    private static string FormatBanSummary(DreamPortLoginResult res)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"封禁原因：{res.BanReason ?? "未提供理由"}");
        if (res.BanUntil is > 0)
        {
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(res.BanUntil.Value).ToLocalTime();
            sb.AppendLine($"解封时间：{dt:yyyy-MM-dd HH:mm:ss}");
        }
        else
        {
            sb.AppendLine("封禁时长：永久");
        }

        if (res.Evidence.Count > 0)
        {
            sb.AppendLine("公开证据附件：");
            foreach (var ev in res.Evidence)
            {
                var label = string.IsNullOrWhiteSpace(ev.Note) ? ev.FileUrl : $"{ev.Note} ({ev.FileUrl})";
                sb.AppendLine("- " + label);
            }
        }

        return sb.ToString().TrimEnd();
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
