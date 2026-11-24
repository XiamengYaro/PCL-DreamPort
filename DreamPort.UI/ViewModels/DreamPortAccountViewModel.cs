using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamPort.Core.Api;
using DreamPort.Core.Auth;
using DreamPort.Core.Config;
using DreamPort.Core.Services;
using DreamPort.UI.Helpers;
using DreamPort.UI.Services;
using PCL.Core.App;

namespace DreamPort.UI.ViewModels;

/// <summary>
/// DreamPort 账号登录（浏览器确认 + 同码二维码）、多设备会话管理、六步入服引导（QQ/MC验证/邀请/AI问卷/申诉）、每日签到积分称号与主页模式视图模型。
/// </summary>
public partial class DreamPortAccountViewModel : ObservableObject, IDisposable
{
    private readonly DreamPortSessionStore _store;
    private readonly DreamPortAutoSigninService _autoSigninService;
    private readonly Action? _onHomepagePreferenceChanged;
    private readonly Action? _onSessionOrSiteChanged;
    private readonly Action<string> _openUrl;
    private readonly Func<string, DreamPortApiClient> _clientFactory;
    private readonly Func<CancellationToken, Task<(string? AccessToken, string? PlayerName, string? Error)>>? _acquireMcToken;
    private readonly Func<string?>? _getActiveMcProfileName;
    private DreamPortApiClient _api;
    private CancellationTokenSource? _waitCts;
    private CancellationTokenSource? _msaCts;
    private bool _initialized;
    private string? _equippedTitleCode;
    private string? _latestProfileStatus;

    [ObservableProperty] private string _baseUrl = DreamPortBuildConfig.BaseUrl;
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private bool _isAdminAccount;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isWaitingBrowserConfirm;
    [ObservableProperty] private string _confirmUrl = string.Empty;
    [ObservableProperty] private Bitmap? _qrCodeBitmap;
    [ObservableProperty] private string _countdownText = string.Empty;
    [ObservableProperty] private string _statusMessage = "未登录 DreamPort 站点账号。";
    [ObservableProperty] private bool _isStatusError;
    [ObservableProperty] private DreamPortHintLevel _hintLevel = DreamPortHintLevel.Info;
    [ObservableProperty] private string _loggedInUsername = string.Empty;
    [ObservableProperty] private string _accountMetaSummary = string.Empty;
    [ObservableProperty] private int _homepageModeIndex;

    // ── 入服引导与六步进度 ──
    [ObservableProperty] private bool _showEntryFlowCard;
    [ObservableProperty] private bool _isApprovedPlayer;
    [ObservableProperty] private bool _canManageSessions;
    [ObservableProperty] private string _progressSummaryText = string.Empty;
    [ObservableProperty] private string _qqBindModeText = string.Empty;
    [ObservableProperty] private string _qqStatusSummary = "尚未查询 QQ 绑定状态";
    [ObservableProperty] private string _qqBindCodeInput = string.Empty;
    [ObservableProperty] private bool _isQqBound;
    [ObservableProperty] private bool _isQqAvailable = true;
    [ObservableProperty] private string _qqUnavailableHint = string.Empty;

    [ObservableProperty] private string _mcStatusSummary = "尚未查询 Minecraft 验证状态";
    [ObservableProperty] private bool _isMcVerified;
    [ObservableProperty] private bool _isMsaFallbackEnabled;
    [ObservableProperty] private bool _isWaitingMsaVerify;
    [ObservableProperty] private string _msaUserCode = string.Empty;
    [ObservableProperty] private string _msaVerificationUri = string.Empty;
    [ObservableProperty] private string _msaWaitHint = string.Empty;
    [ObservableProperty] private string _activeMcProfileName = string.Empty;
    [ObservableProperty] private string _activeMcProfileHint = "未检测到已登录的微软正版档案，请先在「启动」页登录微软正版账号。";

    [ObservableProperty] private string _inviteCodeInput = string.Empty;
    [ObservableProperty] private bool _questionnaireEnabled = true;
    [ObservableProperty] private int _questionnairePassScore;
    [ObservableProperty] private string _questionnaireHintText = "点击「加载入服问卷」获取题目，支持逐题实时 AI 评分。";
    [ObservableProperty] private string _questionnaireSummaryText = string.Empty;
    [ObservableProperty] private bool _isQuestionnaireBusy;

    [ObservableProperty] private bool _isBanned;
    [ObservableProperty] private string _banDetailText = string.Empty;
    [ObservableProperty] private bool _isRejected;
    [ObservableProperty] private string _appealStatusText = string.Empty;
    [ObservableProperty] private bool _hasPendingAppeal;
    [ObservableProperty] private string _appealReasonInput = string.Empty;

    [ObservableProperty] private string _sessionsSummaryText = "登录后可查看与管理各设备会话。";

    // ── 每日签到、积分与称号 ──
    [ObservableProperty] private string _pointsBalanceText = "-";
    [ObservableProperty] private string _signinStatusText = "尚未查询今日签到状态";
    [ObservableProperty] private string _signinStreakText = "-";
    [ObservableProperty] private string _pendingTasksText = "-";
    [ObservableProperty] private string _equippedTitleText = "未佩戴称号";
    [ObservableProperty] private bool _isSignedToday;
    [ObservableProperty] private bool _isSigninBusy;
    [ObservableProperty] private bool _isTitleBusy;
    [ObservableProperty] private DreamPortTitleOption? _selectedTitleOption;

    public ObservableCollection<DreamPortProgressStepVm> StepItems { get; } = [];
    public ObservableCollection<DreamPortSessionItemVm> SessionItems { get; } = [];
    public ObservableCollection<DreamPortQuestionVm> Questions { get; } = [];
    public ObservableCollection<DreamPortScoreFeedbackVm> ScoreFeedbacks { get; } = [];
    public ObservableCollection<DreamPortTitleOption> OwnedTitles { get; } = [];

    public DreamPortAccountViewModel(
        Action? onHomepagePreferenceChanged = null,
        DreamPortSessionStore? store = null,
        Func<string, DreamPortApiClient>? clientFactory = null,
        Action<string>? openUrl = null,
        Func<CancellationToken, Task<(string? AccessToken, string? PlayerName, string? Error)>>? acquireMcToken = null,
        Func<string?>? getActiveMcProfileName = null,
        Action? onSessionOrSiteChanged = null,
        DreamPortAutoSigninService? autoSigninService = null)
    {
        _onHomepagePreferenceChanged = onHomepagePreferenceChanged;
        _onSessionOrSiteChanged = onSessionOrSiteChanged;
        _store = store ?? new DreamPortSessionStore();
        _autoSigninService = autoSigninService ?? new DreamPortAutoSigninService(_store.DataDirectory);
        _clientFactory = clientFactory ?? (url => new DreamPortApiClient(url));
        _openUrl = openUrl ?? (url => Basics.OpenPath(url));
        _acquireMcToken = acquireMcToken;
        _getActiveMcProfileName = getActiveMcProfileName;

        var saved = _store.Load();
        _baseUrl = saved.BaseUrl;
        _loggedInUsername = saved.Username ?? string.Empty;
        _isAdminAccount = saved.IsAdmin;
        _isLoggedIn = !string.IsNullOrEmpty(saved.Token);
        _homepageModeIndex = Math.Clamp(SafeGetDreamPortHomepageMode(), 0, 2);

        _api = _clientFactory(_baseUrl);
        if (!string.IsNullOrEmpty(saved.Token))
            _api.SetToken(saved.Token);

        RefreshActiveMcProfileHint();
        UpdateMetaSummary(saved.IsAdmin, saved.IsTokenPersistent, profile: null);
    }

    public bool IsNotLoggedIn => !IsLoggedIn;
    public bool IsNotBusy => !IsBusy;
    public bool IsNotStatusError => !IsStatusError;
    public bool CanBindQq => IsLoggedIn && IsQqAvailable && !IsQqBound && IsNotBusy;
    public bool CanUnbindQq => IsLoggedIn && IsQqBound && IsNotBusy;
    public bool HasQuestionnaireQuestions => Questions.Count > 0;
    public bool HasScoreFeedbacks => ScoreFeedbacks.Count > 0;
    public bool HasAppealStatus => !string.IsNullOrWhiteSpace(AppealStatusText);
    public bool CanSubmitAppeal => IsRejected && !HasPendingAppeal && IsNotBusy;
    public bool HasSessions => SessionItems.Count > 0;
    public bool HasQqUnavailableHint => !string.IsNullOrWhiteSpace(QqUnavailableHint);
    public bool ShowVerificationSections => ShowEntryFlowCard && !IsBanned && !IsApprovedPlayer;
    public bool HasQuestionnaireSummary => !string.IsNullOrWhiteSpace(QuestionnaireSummaryText);
    public bool HasOwnedTitles => OwnedTitles.Count > 0;
    public bool ShowRewardsCard => IsLoggedIn && !IsAdminAccount;
    public bool CanManualSignin => IsLoggedIn && !IsAdminAccount && IsApprovedPlayer && !IsSignedToday && !IsSigninBusy && IsNotBusy;
    public bool CanApplySelectedTitle => IsLoggedIn && !IsAdminAccount && IsApprovedPlayer && SelectedTitleOption is not null && !IsTitleBusy && IsNotBusy;
    public bool CanUnequipTitle => IsLoggedIn && !IsAdminAccount && IsApprovedPlayer && !string.IsNullOrEmpty(_equippedTitleCode) && !IsTitleBusy && IsNotBusy;

    partial void OnIsLoggedInChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotLoggedIn));
        OnPropertyChanged(nameof(CanBindQq));
        OnPropertyChanged(nameof(CanUnbindQq));
        OnPropertyChanged(nameof(ShowRewardsCard));
        OnPropertyChanged(nameof(CanManualSignin));
        OnPropertyChanged(nameof(CanApplySelectedTitle));
        OnPropertyChanged(nameof(CanUnequipTitle));
    }

    partial void OnIsAdminAccountChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowRewardsCard));
        OnPropertyChanged(nameof(CanManualSignin));
        OnPropertyChanged(nameof(CanApplySelectedTitle));
        OnPropertyChanged(nameof(CanUnequipTitle));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotBusy));
        OnPropertyChanged(nameof(CanBindQq));
        OnPropertyChanged(nameof(CanUnbindQq));
        OnPropertyChanged(nameof(CanSubmitAppeal));
        OnPropertyChanged(nameof(CanManualSignin));
        OnPropertyChanged(nameof(CanApplySelectedTitle));
        OnPropertyChanged(nameof(CanUnequipTitle));
    }

    partial void OnIsStatusErrorChanged(bool value) => OnPropertyChanged(nameof(IsNotStatusError));
    partial void OnIsQqBoundChanged(bool value)
    {
        OnPropertyChanged(nameof(CanBindQq));
        OnPropertyChanged(nameof(CanUnbindQq));
    }
    partial void OnIsQqAvailableChanged(bool value) => OnPropertyChanged(nameof(CanBindQq));
    partial void OnQqUnavailableHintChanged(string value) => OnPropertyChanged(nameof(HasQqUnavailableHint));
    partial void OnIsRejectedChanged(bool value) => OnPropertyChanged(nameof(CanSubmitAppeal));
    partial void OnHasPendingAppealChanged(bool value) => OnPropertyChanged(nameof(CanSubmitAppeal));
    partial void OnAppealStatusTextChanged(string value) => OnPropertyChanged(nameof(HasAppealStatus));
    partial void OnShowEntryFlowCardChanged(bool value) => OnPropertyChanged(nameof(ShowVerificationSections));
    partial void OnIsBannedChanged(bool value) => OnPropertyChanged(nameof(ShowVerificationSections));
    partial void OnIsApprovedPlayerChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowVerificationSections));
        OnPropertyChanged(nameof(CanManualSignin));
        OnPropertyChanged(nameof(CanApplySelectedTitle));
        OnPropertyChanged(nameof(CanUnequipTitle));
    }
    partial void OnQuestionnaireSummaryTextChanged(string value) => OnPropertyChanged(nameof(HasQuestionnaireSummary));
    partial void OnIsSignedTodayChanged(bool value) => OnPropertyChanged(nameof(CanManualSignin));
    partial void OnIsSigninBusyChanged(bool value) => OnPropertyChanged(nameof(CanManualSignin));
    partial void OnIsTitleBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApplySelectedTitle));
        OnPropertyChanged(nameof(CanUnequipTitle));
    }
    partial void OnSelectedTitleOptionChanged(DreamPortTitleOption? value) => OnPropertyChanged(nameof(CanApplySelectedTitle));

    public async Task InitializeAsync()
    {
        if (_initialized)
            return;
        _initialized = true;

        RefreshActiveMcProfileHint();

        var saved = _store.Load();
        if (!string.IsNullOrEmpty(saved.Token))
        {
            await ValidateAndRefreshProfileAsync();
            return;
        }

        if (!string.IsNullOrEmpty(saved.Username) && !saved.IsTokenPersistent)
        {
            SetStatus("上次登录凭据未持久保存（系统加密存储不可用），请通过浏览器重新授权登录。", DreamPortHintLevel.Warning);
        }

        await ProbeConfigAsync();
    }

    [RelayCommand]
    private async Task CheckServerAsync()
    {
        if (!await EnsureClientForBaseUrlAsync())
            return;

        _store.SaveBaseUrlOnly(_api.BaseUrl);
        await ProbeConfigAsync();
    }

    [RelayCommand]
    private void OpenRegisterInBrowser() => OpenSitePathInBrowser("/register", "注册");

    [RelayCommand]
    private void OpenSecuritySettingsInBrowser() => OpenSitePathInBrowser("/dashboard", "账号安全与控制台");

    [RelayCommand]
    private void OpenBanAppealInBrowser() => OpenSitePathInBrowser("/ban-appeal", "封禁申诉");

    [RelayCommand]
    private void OpenTasksCenter() => OpenSitePathInBrowser("/tasks", "任务与兑换中心");

    [RelayCommand]
    private async Task RefreshRewardsAndTitlesAsync()
    {
        if (!IsLoggedIn || IsAdminAccount)
            return;

        IsBusy = true;
        try
        {
            await RefreshRewardsAndTitlesCoreAsync(
                allowAutoSignin: false,
                profileStatus: _latestProfileStatus,
                showErrorInStatus: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ManualSigninAsync()
    {
        if (!IsLoggedIn || IsAdminAccount)
            return;
        if (!IsApprovedPlayer)
        {
            SetStatus("完成入服白名单审核后即可每日签到领取积分。", DreamPortHintLevel.Warning);
            return;
        }

        IsSigninBusy = true;
        try
        {
            var res = await _api.SigninAsync();
            if (!res.Ok)
            {
                if (res.FailureReason == DreamPortFailureReason.Expired)
                {
                    HandleSessionExpired(res.Message);
                    return;
                }

                SetStatus(res.Message, DreamPortHintLevel.Error);
                return;
            }

            if (res.First)
            {
                var pts = res.PointsAwarded > 0 ? $"（+{res.PointsAwarded} 积分）" : "";
                SetStatus($"✓ {res.Message}{pts}", DreamPortHintLevel.Info);
            }
            else
            {
                SetStatus($"✓ {res.Message}", DreamPortHintLevel.Info);
            }

            await RefreshRewardsAndTitlesCoreAsync(
                allowAutoSignin: false,
                profileStatus: _latestProfileStatus,
                showErrorInStatus: false);
        }
        finally
        {
            IsSigninBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplySelectedTitleAsync()
    {
        if (!IsLoggedIn || IsAdminAccount || SelectedTitleOption is null)
            return;

        IsTitleBusy = true;
        try
        {
            var res = await _api.EquipTitleAsync(SelectedTitleOption.Code);
            if (!res.Ok)
            {
                if (res.FailureReason == DreamPortFailureReason.Expired)
                {
                    HandleSessionExpired(res.Message);
                    return;
                }

                SetStatus(res.Message, DreamPortHintLevel.Error);
                return;
            }

            SetStatus($"✓ 已佩戴称号：{SelectedTitleOption.Name}", DreamPortHintLevel.Info);
            await RefreshRewardsAndTitlesCoreAsync(
                allowAutoSignin: false,
                profileStatus: _latestProfileStatus,
                showErrorInStatus: false);
        }
        finally
        {
            IsTitleBusy = false;
        }
    }

    [RelayCommand]
    private async Task UnequipTitleAsync()
    {
        if (!IsLoggedIn || IsAdminAccount)
            return;

        IsTitleBusy = true;
        try
        {
            var res = await _api.UnequipTitleAsync();
            if (!res.Ok)
            {
                if (res.FailureReason == DreamPortFailureReason.Expired)
                {
                    HandleSessionExpired(res.Message);
                    return;
                }

                SetStatus(res.Message, DreamPortHintLevel.Error);
                return;
            }

            SetStatus($"✓ {res.Message}", DreamPortHintLevel.Info);
            await RefreshRewardsAndTitlesCoreAsync(
                allowAutoSignin: false,
                profileStatus: _latestProfileStatus,
                showErrorInStatus: false);
        }
        finally
        {
            IsTitleBusy = false;
        }
    }

    [RelayCommand]
    private async Task StartBrowserLoginAsync()
    {
        if (!await EnsureClientForBaseUrlAsync())
            return;

        CancelActiveLoginWait();
        IsBusy = true;
        SetStatus("正在创建 DreamPort 授权会话...", DreamPortHintLevel.Info);

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
            SetStatus(createResult.error ?? "发起浏览器授权登录失败。", DreamPortHintLevel.Error);
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
            DreamPortHintLevel.Info);

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
            ok ? DreamPortHintLevel.Info : DreamPortHintLevel.Error);
    }

    [RelayCommand]
    private void CancelBrowserLogin()
    {
        CancelActiveLoginWait();
        ClearWaitingState();
        SetStatus("已取消本次浏览器授权登录。", DreamPortHintLevel.Info);
    }

    [RelayCommand]
    private async Task ValidateAndRefreshProfileAsync()
    {
        if (string.IsNullOrEmpty(_api.CurrentToken))
        {
            SetStatus("当前未登录。", DreamPortHintLevel.Error);
            return;
        }

        IsBusy = true;
        try
        {
            RefreshActiveMcProfileHint();
            var valid = await _api.ValidateSessionAsync();
            if (!valid.Valid)
            {
                if (valid.Reason == DreamPortFailureReason.Network)
                {
                    SetStatus($"网络不可达：{valid.Message ?? "请检查网络"}（已保留本地登录状态）", DreamPortHintLevel.Error);
                    return;
                }

                HandleSessionExpired(valid.Message ?? "登录已过期，请通过浏览器重新授权登录。");
                return;
            }

            var saved = _store.Save(_api.BaseUrl, valid.Username ?? LoggedInUsername, valid.IsAdmin, _api.CurrentToken);
            IsLoggedIn = true;
            IsAdminAccount = saved.IsAdmin;
            LoggedInUsername = saved.Username ?? string.Empty;

            await RefreshAccountStateAsync(saved.IsAdmin, saved.IsTokenPersistent, showSuccessBanner: true);
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
        CancelMsaVerifyInternal();
        ClearWaitingState();
        IsBusy = true;
        try
        {
            await _api.LogoutAsync();
            _store.ClearToken(_api.BaseUrl);
            IsLoggedIn = false;
            IsAdminAccount = false;
            LoggedInUsername = string.Empty;
            AccountMetaSummary = string.Empty;
            ResetAccountDerivedState();
            _onSessionOrSiteChanged?.Invoke();
            SetStatus("已退出登录并吊销当前设备会话。", DreamPortHintLevel.Info);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshSessionsAsync()
    {
        if (!IsLoggedIn)
        {
            SetStatus("请先登录 DreamPort 账号。", DreamPortHintLevel.Error);
            return;
        }

        if (IsAdminAccount)
        {
            SessionsSummaryText = "管理员会话由独立后台安全域托管，不在此列表管理。";
            SessionItems.Clear();
            OnPropertyChanged(nameof(HasSessions));
            return;
        }

        IsBusy = true;
        try
        {
            await LoadSessionsCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RevokeSingleSessionAsync(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        if (!CanManageSessions)
        {
            SetStatus("账号通过白名单审核后才能踢出其他设备（当前可直接使用本页的退出登录）。", DreamPortHintLevel.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            var res = await _api.RevokeSessionAsync(sessionId);
            if (!res.Ok)
            {
                if (res.FailureReason == DreamPortFailureReason.Expired)
                {
                    HandleSessionExpired(res.Message);
                    return;
                }

                SetStatus(res.Message, DreamPortHintLevel.Error);
                return;
            }

            SetStatus($"✓ {res.Message}", DreamPortHintLevel.Info);
            await LoadSessionsCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RevokeOtherSessionsAsync()
    {
        if (!CanManageSessions)
        {
            SetStatus("账号通过白名单审核后才能批量管理其他登录设备。", DreamPortHintLevel.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            var res = await _api.RevokeSessionAsync(sessionId: null);
            if (!res.Ok)
            {
                if (res.FailureReason == DreamPortFailureReason.Expired)
                {
                    HandleSessionExpired(res.Message);
                    return;
                }

                SetStatus(res.Message, DreamPortHintLevel.Error);
                return;
            }

            SetStatus($"✓ {res.Message}", DreamPortHintLevel.Info);
            await LoadSessionsCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task BindQqAsync()
    {
        if (!IsLoggedIn)
        {
            SetStatus("请先登录 DreamPort 账号。", DreamPortHintLevel.Error);
            return;
        }

        var code = QqBindCodeInput.Trim();
        if (string.IsNullOrEmpty(code))
        {
            SetStatus("请先在官方 QQ 群发送「/dp 绑定」，并输入机器人私聊回复的验证码。", DreamPortHintLevel.Error);
            return;
        }

        IsBusy = true;
        try
        {
            var res = await _api.BindQqAsync(code);
            if (!res.Ok)
            {
                if (res.FailureReason == DreamPortFailureReason.Expired)
                {
                    HandleSessionExpired(res.Message);
                    return;
                }

                SetStatus(res.Message, DreamPortHintLevel.Error);
                return;
            }

            QqBindCodeInput = string.Empty;
            SetStatus($"✓ {res.Message}", DreamPortHintLevel.Info);
            await RefreshEntryFlowAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UnbindQqAsync()
    {
        if (!IsLoggedIn)
        {
            SetStatus("请先登录 DreamPort 账号。", DreamPortHintLevel.Error);
            return;
        }

        IsBusy = true;
        try
        {
            var res = await _api.UnbindQqAsync();
            if (!res.Ok)
            {
                if (res.FailureReason == DreamPortFailureReason.Expired)
                {
                    HandleSessionExpired(res.Message);
                    return;
                }

                SetStatus(res.Message, DreamPortHintLevel.Error);
                return;
            }

            SetStatus($"✓ {res.Message}", DreamPortHintLevel.Info);
            await RefreshEntryFlowAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task VerifyMinecraftWithCurrentProfileAsync()
    {
        if (!IsLoggedIn)
        {
            SetStatus("请先登录 DreamPort 账号。", DreamPortHintLevel.Error);
            return;
        }

        if (_acquireMcToken is null)
        {
            SetStatus("当前宿主未接入微软档案令牌读取器，请先使用下方微软设备码备用验证。", DreamPortHintLevel.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            SetStatus("正在读取当前启动器微软正版令牌并提交验证...", DreamPortHintLevel.Info);
            var (accessToken, playerName, err) = await _acquireMcToken(CancellationToken.None);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                SetStatus(err ?? "未获取到有效微软 Minecraft 令牌，请先在「启动」页登录微软正版账号。", DreamPortHintLevel.Error);
                return;
            }

            var res = await _api.VerifyMinecraftByTokenAsync(accessToken);
            if (!res.Ok)
            {
                if (res.FailureReason == DreamPortFailureReason.Expired)
                {
                    HandleSessionExpired(res.Message);
                    return;
                }

                SetStatus(res.Message, DreamPortHintLevel.Error);
                return;
            }

            var who = string.IsNullOrWhiteSpace(playerName) ? "" : $"（角色 {playerName}）";
            SetStatus($"✓ {res.Message}{who}", DreamPortHintLevel.Info);
            await RefreshAccountStateAsync(IsAdminAccount, _store.Load().IsTokenPersistent, showSuccessBanner: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StartMsaVerifyAsync()
    {
        if (!IsLoggedIn)
        {
            SetStatus("请先登录 DreamPort 账号。", DreamPortHintLevel.Error);
            return;
        }

        CancelMsaVerifyInternal();
        IsBusy = true;
        DreamPortMsaStartResult start;
        try
        {
            start = await _api.StartMsaVerifyAsync();
        }
        finally
        {
            IsBusy = false;
        }

        if (!start.Ok || string.IsNullOrWhiteSpace(start.FlowId) || string.IsNullOrWhiteSpace(start.UserCode))
        {
            if (start.FailureReason == DreamPortFailureReason.Expired)
            {
                HandleSessionExpired(start.Message ?? "登录已过期，请重新登录。");
                return;
            }

            SetStatus(start.Message ?? "发起微软设备码验证失败。", DreamPortHintLevel.Error);
            return;
        }

        MsaUserCode = start.UserCode;
        MsaVerificationUri = string.IsNullOrWhiteSpace(start.VerificationUri)
            ? "https://www.microsoft.com/link"
            : start.VerificationUri;
        MsaWaitHint = $"请在浏览器中输入设备码 {start.UserCode} 完成微软登录（约 {Math.Max(1, start.ExpiresInSec / 60)} 分钟有效）";
        IsWaitingMsaVerify = true;
        TryOpenBrowser(MsaVerificationUri);
        SetStatus("已发起微软设备码正版验证，请在打开的微软页面输入设备码完成确认。", DreamPortHintLevel.Info);

        var cts = new CancellationTokenSource();
        _msaCts = cts;
        _ = PollMsaVerifyLoopAsync(start.FlowId, start.IntervalSec, cts);
    }

    [RelayCommand]
    private void ReopenMsaBrowser()
    {
        if (!string.IsNullOrWhiteSpace(MsaVerificationUri))
            TryOpenBrowser(MsaVerificationUri);
    }

    [RelayCommand]
    private void CancelMsaVerify()
    {
        CancelMsaVerifyInternal();
        SetStatus("已取消微软设备码正版验证。", DreamPortHintLevel.Info);
    }

    [RelayCommand]
    private async Task ApplyInviteCodeAsync()
    {
        if (!IsLoggedIn)
        {
            SetStatus("请先登录 DreamPort 账号。", DreamPortHintLevel.Error);
            return;
        }

        var code = InviteCodeInput.Trim();
        if (string.IsNullOrEmpty(code))
        {
            SetStatus("请输入邀请码。", DreamPortHintLevel.Error);
            return;
        }

        IsBusy = true;
        try
        {
            var res = await _api.ApplyInviteCodeAsync(code);
            if (!res.Ok)
            {
                if (res.FailureReason == DreamPortFailureReason.Expired)
                {
                    HandleSessionExpired(res.Message);
                    return;
                }

                SetStatus(res.Message, DreamPortHintLevel.Error);
                return;
            }

            InviteCodeInput = string.Empty;
            SetStatus($"✓ {res.Message}", DreamPortHintLevel.Info);
            await RefreshAccountStateAsync(IsAdminAccount, _store.Load().IsTokenPersistent, showSuccessBanner: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadQuestionnaireAsync()
    {
        if (!IsLoggedIn)
        {
            SetStatus("请先登录 DreamPort 账号。", DreamPortHintLevel.Error);
            return;
        }

        IsBusy = true;
        try
        {
            var (cfg, reason, error) = await _api.GetQuestionnaireConfigAsync();
            if (cfg is null)
            {
                if (reason == DreamPortFailureReason.Expired)
                {
                    HandleSessionExpired(error ?? "登录已过期，请重新登录。");
                    return;
                }

                SetStatus(error ?? "加载入服问卷失败。", DreamPortHintLevel.Error);
                return;
            }

            QuestionnaireEnabled = cfg.Enabled;
            QuestionnairePassScore = cfg.PassScore;
            Questions.Clear();
            ScoreFeedbacks.Clear();
            OnPropertyChanged(nameof(HasScoreFeedbacks));
            QuestionnaireSummaryText = string.Empty;

            if (!cfg.Enabled || cfg.Questions.Count == 0)
            {
                QuestionnaireHintText = "当前站点未开放或未配置入服问卷题目，可通过邀请码或等待管理审核。";
                OnPropertyChanged(nameof(HasQuestionnaireQuestions));
                SetStatus("已刷新入服问卷配置：当前无需作答问卷。", DreamPortHintLevel.Info);
                return;
            }

            for (var i = 0; i < cfg.Questions.Count; i++)
            {
                var q = cfg.Questions[i];
                Questions.Add(new DreamPortQuestionVm(
                    order: i + 1,
                    id: q.Id,
                    text: q.Text,
                    type: q.Type,
                    required: q.Required,
                    maxScore: q.MaxScore,
                    multiline: q.Multiline,
                    minLength: q.MinLength,
                    maxLength: q.MaxLength,
                    minSelections: q.MinSelections,
                    maxSelections: q.MaxSelections,
                    placeholder: q.Placeholder,
                    options: q.Options));
            }

            QuestionnaireHintText = $"及格线：{cfg.PassScore} 分（满分按题库总分百分比折算） · 共 {cfg.Questions.Count} 题";
            OnPropertyChanged(nameof(HasQuestionnaireQuestions));
            SetStatus($"已加载入服问卷（共 {cfg.Questions.Count} 题），完成后点击提交将实时展示 AI 评分。", DreamPortHintLevel.Info);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SubmitQuestionnaireAsync()
    {
        if (!IsLoggedIn)
        {
            SetStatus("请先登录 DreamPort 账号。", DreamPortHintLevel.Error);
            return;
        }

        if (Questions.Count == 0)
        {
            SetStatus("请先点击「加载入服问卷」。", DreamPortHintLevel.Error);
            return;
        }

        if (!TryBuildQuestionnaireAnswers(out var answers, out var validationError))
        {
            SetStatus(validationError!, DreamPortHintLevel.Error);
            return;
        }

        IsBusy = true;
        IsQuestionnaireBusy = true;
        ScoreFeedbacks.Clear();
        OnPropertyChanged(nameof(HasScoreFeedbacks));
        QuestionnaireSummaryText = "正在连接 AI 评分服务，逐题评估中...";

        var uiCtx = SynchronizationContext.Current;
        void DispatchUi(Action action)
        {
            if (uiCtx is not null && !ReferenceEquals(SynchronizationContext.Current, uiCtx))
                uiCtx.Post(_ => action(), null);
            else
                action();
        }

        try
        {
            var outcome = await _api.SubmitQuestionnaireStreamAsync(
                answers,
                onScored: ev => DispatchUi(() =>
                {
                    ScoreFeedbacks.Add(new DreamPortScoreFeedbackVm(
                        HeaderText: $"第 {ev.Index + 1}/{Math.Max(1, ev.Total)} 题 · {ev.QuestionText}",
                        ScoreText: $"得分 {ev.Score}/{ev.MaxScore}（累计 {ev.TotalScore}/{ev.MaxScoreTotal}）",
                        ReasonText: string.IsNullOrWhiteSpace(ev.Reason) ? "已完成评分" : ev.Reason));
                    OnPropertyChanged(nameof(HasScoreFeedbacks));
                }),
                onSummarizing: () => DispatchUi(() =>
                    QuestionnaireSummaryText = "逐题评分完成，正在生成综合评语..."));

            if (!outcome.Ok)
            {
                if (outcome.FailureReason == DreamPortFailureReason.Expired)
                {
                    HandleSessionExpired(outcome.ErrorMessage ?? "登录已过期，请通过浏览器重新授权登录。");
                    return;
                }

                QuestionnaireSummaryText = outcome.ErrorMessage ?? "问卷提交失败。";
                SetStatus(QuestionnaireSummaryText, DreamPortHintLevel.Error);
                return;
            }

            var statusTag = outcome.Passed ? "已通过问卷评测" : "未达及格线";
            QuestionnaireSummaryText = $"{statusTag} · 得分 {outcome.TotalScore}/{outcome.MaxScore}\n{outcome.Summary}".Trim();
            SetStatus(
                outcome.Passed
                    ? $"✓ 问卷提交成功（{outcome.TotalScore}/{outcome.MaxScore} 分，已通过）！"
                    : $"问卷已提交（{outcome.TotalScore}/{outcome.MaxScore} 分，未及格，可查看评语或修改后重交）。",
                outcome.Passed ? DreamPortHintLevel.Info : DreamPortHintLevel.Warning);

            await RefreshAccountStateAsync(IsAdminAccount, _store.Load().IsTokenPersistent, showSuccessBanner: false);
        }
        finally
        {
            IsQuestionnaireBusy = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SubmitAppealAsync()
    {
        if (!IsLoggedIn)
        {
            SetStatus("请先登录 DreamPort 账号。", DreamPortHintLevel.Error);
            return;
        }

        var reason = AppealReasonInput.Trim();
        if (reason.Length < 10)
        {
            SetStatus("申诉理由至少需要 10 个字，请写明具体题目与情况。", DreamPortHintLevel.Error);
            return;
        }

        IsBusy = true;
        try
        {
            var res = await _api.SubmitAppealAsync(reason);
            if (!res.Ok)
            {
                if (res.FailureReason == DreamPortFailureReason.Expired)
                {
                    HandleSessionExpired(res.Message);
                    return;
                }

                SetStatus(res.Message, DreamPortHintLevel.Error);
                return;
            }

            AppealReasonInput = string.Empty;
            SetStatus($"✓ {res.Message}", DreamPortHintLevel.Info);
            await RefreshAppealCoreAsync();
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
        SetStatus($"✓ 主页模式已切换为「{label}」，并已触发主页刷新。", DreamPortHintLevel.Info);
    }

    public void Dispose()
    {
        CancelActiveLoginWait();
        CancelMsaVerifyInternal();
        _api.Dispose();
    }

    private bool TryBuildQuestionnaireAnswers(
        out Dictionary<string, object> answers,
        out string? error)
    {
        answers = new Dictionary<string, object>(StringComparer.Ordinal);
        error = null;

        foreach (var q in Questions)
        {
            var key = q.Id.ToString();
            if (q.IsTextQuestion)
            {
                var val = (q.AnswerText ?? string.Empty).Trim();
                if (q.Required && string.IsNullOrEmpty(val))
                {
                    error = $"第 {q.Order} 题为必答题，请填写内容。";
                    return false;
                }
                if (!string.IsNullOrEmpty(val) && q.MinLength > 0 && val.Length < q.MinLength)
                {
                    error = $"第 {q.Order} 题至少需要 {q.MinLength} 字。";
                    return false;
                }
                if (q.MaxLength > 0 && val.Length > q.MaxLength)
                {
                    error = $"第 {q.Order} 题最多 {q.MaxLength} 字。";
                    return false;
                }
                answers[key] = val;
            }
            else if (q.Type == "single")
            {
                var chosen = q.Options.FirstOrDefault(o => o.IsSelected);
                if (q.Required && chosen is null)
                {
                    error = $"第 {q.Order} 题为必答单选题，请选择 1 项。";
                    return false;
                }
                if (chosen is not null)
                    answers[key] = chosen.Id;
            }
            else
            {
                var selectedIds = q.Options.Where(o => o.IsSelected).Select(o => o.Id).ToList();
                if (q.Required && selectedIds.Count == 0)
                {
                    error = $"第 {q.Order} 题为必答多选题，请至少选择 1 项。";
                    return false;
                }
                if (q.MinSelections > 0 && selectedIds.Count < q.MinSelections)
                {
                    error = $"第 {q.Order} 题至少需要选择 {q.MinSelections} 项。";
                    return false;
                }
                if (q.MaxSelections > 0 && selectedIds.Count > q.MaxSelections)
                {
                    error = $"第 {q.Order} 题最多选择 {q.MaxSelections} 项。";
                    return false;
                }
                answers[key] = selectedIds;
            }
        }

        return true;
    }

    private async Task RefreshAccountStateAsync(
        bool isAdmin,
        bool isTokenPersistent,
        bool showSuccessBanner,
        CancellationToken ct = default)
    {
        RefreshActiveMcProfileHint();

        var profile = await _api.GetProfileAsync(ct);
        _latestProfileStatus = profile?.Status;
        UpdateMetaSummary(isAdmin, isTokenPersistent, profile);

        if (isAdmin)
        {
            IsApprovedPlayer = false;
            CanManageSessions = false;
            ShowEntryFlowCard = false;
            SessionsSummaryText = "管理员会话由独立后台安全域托管，不在此列表管理。";
            SessionItems.Clear();
            OnPropertyChanged(nameof(HasSessions));
            ResetRewardsAndTitlesState();
            DreamPortShellBridge.ResetUnreadCount();
            if (showSuccessBanner)
                SetStatus("✓ 已连接 DreamPort 站点（管理员身份）。", DreamPortHintLevel.Info);
            return;
        }

        var progressRes = await _api.GetProgressAsync(ct);
        if (!progressRes.Ok)
        {
            if (progressRes.FailureReason == DreamPortFailureReason.Expired)
            {
                HandleSessionExpired(progressRes.Message ?? "登录已过期，请通过浏览器重新授权登录。");
                return;
            }

            if (showSuccessBanner)
                SetStatus(progressRes.Message ?? "已校验登录会话，但获取入服进度遇到网络波动。", DreamPortHintLevel.Warning);
        }
        else if (progressRes.Progress is not null)
        {
            ApplyProgress(progressRes.Progress);
            await RefreshEntryFlowDetailsAsync(progressRes.Progress, ct);
        }

        await LoadSessionsCoreAsync(ct);
        var autoSignedNow = await RefreshRewardsAndTitlesCoreAsync(
            allowAutoSignin: IsApprovedPlayer && string.Equals(profile?.Status, "approved", StringComparison.OrdinalIgnoreCase),
            profileStatus: profile?.Status,
            showErrorInStatus: false,
            ct);

        if (showSuccessBanner)
        {
            if (IsBanned)
                SetStatus("当前账号处于封禁状态，请查看下方封禁说明或前往网页端申诉。", DreamPortHintLevel.Error);
            else if (IsRejected)
                SetStatus("入服审核未通过，可在下方提交人工复核申诉或查看管理员回复。", DreamPortHintLevel.Warning);
            else if (ShowEntryFlowCard)
                SetStatus("已登录 DreamPort 站点，请按下方入服引导完成待办步骤。", DreamPortHintLevel.Warning);
            else if (autoSignedNow)
                SetStatus("✓ 已连接 DreamPort 站点并完成每日自动签到。", DreamPortHintLevel.Info);
            else
                SetStatus("✓ 已连接 DreamPort 站点并完成账号与会话校验。", DreamPortHintLevel.Info);
        }
    }

    private async Task RefreshEntryFlowAsync(CancellationToken ct = default)
    {
        if (IsAdminAccount)
            return;

        var progressRes = await _api.GetProgressAsync(ct);
        if (progressRes.Ok && progressRes.Progress is not null)
        {
            ApplyProgress(progressRes.Progress);
            await RefreshEntryFlowDetailsAsync(progressRes.Progress, ct);
        }
        else if (progressRes.FailureReason == DreamPortFailureReason.Expired)
        {
            HandleSessionExpired(progressRes.Message ?? "登录已过期，请通过浏览器重新登录。");
        }

        var profile = await _api.GetProfileAsync(ct);
        UpdateMetaSummary(IsAdminAccount, _store.Load().IsTokenPersistent, profile);
    }

    private async Task RefreshEntryFlowDetailsAsync(DreamPortUserProgress progress, CancellationToken ct)
    {
        if (progress.ShowBar || progress.BindHint)
        {
            var qqTask = _api.GetQqStatusAsync(ct);
            var mcTask = _api.GetMinecraftStatusAsync(ct);
            await Task.WhenAll(qqTask, mcTask);

            var qq = await qqTask;
            if (qq.Status is not null)
            {
                IsQqBound = qq.Status.Bound;
                if (qq.Status.Bound)
                {
                    QqStatusSummary = $"已绑定 QQ：{qq.Status.MaskedQq}";
                }
                else
                {
                    var pendingHint = string.IsNullOrWhiteSpace(qq.Status.PendingQq)
                        ? ""
                        : $"（注册预填：{qq.Status.PendingQq}，仍需群内取码验证）";
                    QqStatusSummary = $"未绑定 QQ{pendingHint}。请在 QQ 群发送「/dp 绑定」，再把机器人私聊给你的验证码填到下方。";
                }
            }

            var mc = await mcTask;
            if (mc.Status is not null)
            {
                IsMcVerified = mc.Status.Verified;
                IsMsaFallbackEnabled = mc.Status.MsaEnabled;
                McStatusSummary = mc.Status.Verified
                    ? $"✓ 已绑定并验证正版 ID：{mc.Status.MinecraftName ?? "-"}"
                    : string.IsNullOrWhiteSpace(mc.Status.MinecraftName)
                        ? "尚未绑定并验证正版 Minecraft ID。"
                        : $"已填 ID「{mc.Status.MinecraftName}」，等待正版所有权验证。";
            }
        }

        if (progress.Rejected)
            await RefreshAppealCoreAsync(ct);
    }

    private async Task RefreshAppealCoreAsync(CancellationToken ct = default)
    {
        var (appeal, reason, _) = await _api.GetMyAppealAsync(ct);
        if (reason == DreamPortFailureReason.Expired)
        {
            HandleSessionExpired("登录已过期，请重新登录。");
            return;
        }

        if (appeal is null || !appeal.Found)
        {
            HasPendingAppeal = false;
            AppealStatusText = "当前尚未提交过问卷复核申诉，可在下方填写至少 10 字理由提交。";
            return;
        }

        HasPendingAppeal = string.Equals(appeal.Status, "pending", StringComparison.OrdinalIgnoreCase);
        var stateLabel = appeal.Status switch
        {
            "pending" => "待管理员处理",
            "approved" => "申诉已通过",
            "rejected" => "申诉未通过",
            _ => appeal.Status
        };
        AppealStatusText = string.IsNullOrWhiteSpace(appeal.AdminReply)
            ? $"最近申诉状态：{stateLabel}"
            : $"最近申诉状态：{stateLabel} | 管理员回复：{appeal.AdminReply}";
    }

    private void ApplyProgress(DreamPortUserProgress progress)
    {
        IsApprovedPlayer = !progress.ShowBar && !progress.Rejected && !progress.Banned;
        CanManageSessions = IsLoggedIn && !IsAdminAccount && IsApprovedPlayer;
        ShowEntryFlowCard = IsLoggedIn && !IsAdminAccount && (progress.ShowBar || progress.BindHint || progress.Rejected || progress.Banned);

        IsBanned = progress.Banned;
        BanDetailText = progress.Banned
            ? $"封禁原因：{progress.BanReason ?? "违反服务器规则"}"
              + (progress.BanUntilMs is { } until && until > 0
                  ? $"（截止：{DateTimeOffset.FromUnixTimeMilliseconds(until).ToLocalTime():yyyy-MM-dd HH:mm}）"
                  : "（永久封禁）")
            : string.Empty;

        IsRejected = progress.Rejected;

        var modeDesc = progress.QqBindMode switch
        {
            "required" => "QQ 绑定：必选",
            "optional" => "QQ 绑定：可选",
            "off" => "QQ 绑定：已关闭",
            _ => $"QQ 绑定：{progress.QqBindMode}"
        };
        QqBindModeText = modeDesc;

        var qqStep = progress.Steps.FirstOrDefault(s => s.Key == "qq");
        IsQqAvailable = qqStep?.Available ?? true;
        QqUnavailableHint = qqStep is { Available: false } ? qqStep.UnavailableReason : string.Empty;

        ProgressSummaryText = progress.BindHint && !progress.ShowBar
            ? "当前账号已获白名单，建议顺便绑定 QQ 以便接收群内通知与使用指令。"
            : $"入服进度：{progress.DoneCount}/{Math.Max(1, progress.TotalCount)} 步";

        StepItems.Clear();
        foreach (var step in progress.Steps)
        {
            StepItems.Add(new DreamPortProgressStepVm(
                Key: step.Key,
                Label: step.Label,
                Done: step.Done,
                IsCurrent: !step.Done && string.Equals(step.Key, progress.CurrentStep, StringComparison.OrdinalIgnoreCase),
                Available: step.Available,
                UnavailableReason: step.UnavailableReason,
                Via: step.Via));
        }
    }

    private async Task LoadSessionsCoreAsync(CancellationToken ct = default)
    {
        var sessRes = await _api.GetSessionsAsync(ct);
        if (!sessRes.Ok)
        {
            if (sessRes.FailureReason == DreamPortFailureReason.Expired)
            {
                HandleSessionExpired(sessRes.Message ?? "登录已过期，请重新登录。");
                return;
            }

            SessionsSummaryText = sessRes.Message ?? "读取设备列表失败";
            return;
        }

        SessionItems.Clear();
        var active = sessRes.Sessions
            .Where(s => !s.Revoked)
            .OrderByDescending(s => s.IsCurrent)
            .ThenByDescending(s => s.LastSeenAtMs)
            .ToList();

        foreach (var s in active)
        {
            SessionItems.Add(new DreamPortSessionItemVm(
                sessionId: s.SessionId,
                deviceName: s.DeviceName,
                platform: s.Platform,
                clientVersion: s.ClientVersion,
                ip: s.Ip,
                lastSeenAtMs: s.LastSeenAtMs,
                revoked: s.Revoked,
                isCurrent: s.IsCurrent,
                revokeCommand: new AsyncRelayCommand(() => RevokeSingleSessionAsync(s.SessionId))));
        }

        OnPropertyChanged(nameof(HasSessions));
        SessionsSummaryText = active.Count == 0
            ? "当前没有活跃设备会话记录。"
            : CanManageSessions
                ? $"当前共有 {active.Count} 台活跃登录设备（支持单设备踢下线或踢除其它设备）。"
                : $"当前共有 {active.Count} 台活跃登录设备（完成入服审核后可踢除其他设备）。";
    }

    private void RefreshActiveMcProfileHint()
    {
        var name = _getActiveMcProfileName?.Invoke();
        ActiveMcProfileName = name ?? string.Empty;
        ActiveMcProfileHint = string.IsNullOrWhiteSpace(name)
            ? "当前未选定微软正版档案。请先前往「启动」页登录微软正版账号。"
            : $"当前启动器已登录微软正版角色：{name}（可直接一键提交验证）";
    }

    private async Task PollMsaVerifyLoopAsync(string flowId, int intervalSec, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var delaySec = Math.Clamp(intervalSec, 2, 15);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySec), ct);
                var poll = await _api.PollMsaVerifyAsync(flowId, ct);
                if (poll.IntervalSec > 0)
                    delaySec = Math.Clamp(poll.IntervalSec, 2, 15);

                switch (poll.Status)
                {
                    case "verified":
                    case "completed":
                    case "success":
                        CancelMsaVerifyInternal();
                        SetStatus($"✓ {poll.Message ?? "微软正版验证完成！"}", DreamPortHintLevel.Info);
                        await RefreshAccountStateAsync(IsAdminAccount, _store.Load().IsTokenPersistent, showSuccessBanner: false);
                        return;

                    case "expired":
                    case "denied":
                    case "failed":
                        CancelMsaVerifyInternal();
                        if (poll.FailureReason == DreamPortFailureReason.Expired)
                        {
                            HandleSessionExpired(poll.Message ?? "登录已过期，请重新登录。");
                            return;
                        }
                        SetStatus(poll.Message ?? $"微软设备码验证结束（{poll.Status}）。", DreamPortHintLevel.Error);
                        return;

                    case "network":
                        MsaWaitHint = $"网络波动，仍在等待设备码 {MsaUserCode} 确认...";
                        continue;

                    default:
                        MsaWaitHint = $"正在等待你在微软页面确认设备码 {MsaUserCode}...";
                        continue;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 用户取消或新流程覆盖
        }
        finally
        {
            if (ReferenceEquals(_msaCts, cts))
            {
                _msaCts.Dispose();
                _msaCts = null;
            }
        }
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
                SetStatus("登录授权已超时（5 分钟有效），请重新点击「在浏览器中登录」。", DreamPortHintLevel.Error);
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
                        IsAdminAccount = saved.IsAdmin;
                        LoggedInUsername = saved.Username ?? string.Empty;

                        await RefreshAccountStateAsync(saved.IsAdmin, saved.IsTokenPersistent, showSuccessBanner: false, ct);
                        _onSessionOrSiteChanged?.Invoke();

                        var suffix = saved.IsTokenPersistent ? "凭据已加密保存。" : "系统加密存储不可用，本次令牌仅保留在内存中。";
                        var flowHint = ShowEntryFlowCard ? " 请按下方引导完成入服步骤。" : "";
                        SetStatus($"✓ 授权登录成功，欢迎 {LoggedInUsername}！{suffix}{flowHint}", DreamPortHintLevel.Info);
                        return;
                    }

                    if (consumed.Status == DreamPortBrowserLoginStatus.NetworkError)
                    {
                        SetStatus("已确认授权，获取令牌时遇到网络波动，正在自动重试...", DreamPortHintLevel.Info);
                        await Task.Delay(TimeSpan.FromSeconds(2), ct);
                        continue;
                    }

                    ClearWaitingState();
                    SetStatus(consumed.Message ?? "授权凭据已失效，请重新登录。", DreamPortHintLevel.Error);
                    return;
                }
                case DreamPortBrowserLoginStatus.Denied:
                    ClearWaitingState();
                    SetStatus("已在确认端拒绝此次登录请求。", DreamPortHintLevel.Error);
                    return;

                case DreamPortBrowserLoginStatus.Expired:
                case DreamPortBrowserLoginStatus.NotFound:
                    ClearWaitingState();
                    SetStatus(poll.Message ?? "本次登录授权已过期，请重新点击「在浏览器中登录」。", DreamPortHintLevel.Error);
                    return;

                case DreamPortBrowserLoginStatus.Consumed:
                    ClearWaitingState();
                    SetStatus("该登录授权凭据已被使用，请重新发起。", DreamPortHintLevel.Error);
                    return;

                case DreamPortBrowserLoginStatus.NetworkError:
                    SetStatus("连接站点遇到短暂波动，仍在等待浏览器确认...", DreamPortHintLevel.Info);
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
                SetStatus($"未能获取站点配置（{_api.BaseUrl}），仅可离线使用基础功能。", DreamPortHintLevel.Warning);
                return;
            }

            var name = string.IsNullOrWhiteSpace(cfg.SiteName) ? _api.BaseUrl : cfg.SiteName;
            SetStatus($"✓ 已连通站点：{name}", DreamPortHintLevel.Info);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenSitePathInBrowser(string relativePath, string label)
    {
        if (!DreamPortApiClient.TryNormalizeBaseUrl(BaseUrl, out var normalized, out var err))
        {
            SetStatus(err ?? "站点地址格式无效。", DreamPortHintLevel.Error);
            return;
        }

        var url = normalized + relativePath;
        var ok = TryOpenBrowser(url);
        SetStatus(
            ok ? $"已在系统浏览器打开{label}页面（{url}）。" : $"无法自动打开浏览器，请手动访问：{url}",
            ok ? DreamPortHintLevel.Info : DreamPortHintLevel.Warning);
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

    private void CancelMsaVerifyInternal()
    {
        var existing = _msaCts;
        _msaCts = null;
        if (existing is not null)
        {
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

        IsWaitingMsaVerify = false;
        MsaUserCode = string.Empty;
        MsaVerificationUri = string.Empty;
        MsaWaitHint = string.Empty;
    }

    private void ClearWaitingState()
    {
        IsWaitingBrowserConfirm = false;
        ConfirmUrl = string.Empty;
        QrCodeBitmap = null;
        CountdownText = string.Empty;
    }

    private void HandleSessionExpired(string message)
    {
        CancelMsaVerifyInternal();
        _api.SetToken(null);
        _store.ClearToken(_api.BaseUrl);
        IsLoggedIn = false;
        IsAdminAccount = false;
        LoggedInUsername = string.Empty;
        AccountMetaSummary = string.Empty;
        ResetAccountDerivedState();
        SetStatus(message, DreamPortHintLevel.Error);
    }

    private void ResetAccountDerivedState()
    {
        ShowEntryFlowCard = false;
        IsApprovedPlayer = false;
        CanManageSessions = false;
        ProgressSummaryText = string.Empty;
        QqBindModeText = string.Empty;
        QqStatusSummary = "尚未查询 QQ 绑定状态";
        QqBindCodeInput = string.Empty;
        IsQqBound = false;
        IsQqAvailable = true;
        QqUnavailableHint = string.Empty;
        McStatusSummary = "尚未查询 Minecraft 验证状态";
        IsMcVerified = false;
        IsMsaFallbackEnabled = false;
        InviteCodeInput = string.Empty;
        Questions.Clear();
        ScoreFeedbacks.Clear();
        OnPropertyChanged(nameof(HasQuestionnaireQuestions));
        OnPropertyChanged(nameof(HasScoreFeedbacks));
        QuestionnaireSummaryText = string.Empty;
        IsBanned = false;
        BanDetailText = string.Empty;
        IsRejected = false;
        AppealStatusText = string.Empty;
        HasPendingAppeal = false;
        AppealReasonInput = string.Empty;
        SessionItems.Clear();
        OnPropertyChanged(nameof(HasSessions));
        SessionsSummaryText = "登录后可查看与管理各设备会话。";
        StepItems.Clear();
        _latestProfileStatus = null;
        ResetRewardsAndTitlesState();
        DreamPortShellBridge.ResetUnreadCount();
    }

    private void ResetRewardsAndTitlesState()
    {
        PointsBalanceText = "-";
        SigninStatusText = "尚未查询今日签到状态";
        SigninStreakText = "-";
        PendingTasksText = "-";
        _equippedTitleCode = null;
        EquippedTitleText = "未佩戴称号";
        IsSignedToday = false;
        OwnedTitles.Clear();
        SelectedTitleOption = null;
        OnPropertyChanged(nameof(HasOwnedTitles));
        OnPropertyChanged(nameof(CanUnequipTitle));
    }

    private async Task<bool> RefreshRewardsAndTitlesCoreAsync(
        bool allowAutoSignin,
        string? profileStatus,
        bool showErrorInStatus,
        CancellationToken ct = default)
    {
        var centerTask = _api.GetPointsCenterAsync(ct);
        var titlesTask = _api.GetMyTitlesAsync(ct);
        var unreadTask = _api.GetUnreadNotificationCountAsync(ct);
        await Task.WhenAll(centerTask, titlesTask, unreadTask);

        var center = await centerTask;
        var titles = await titlesTask;
        var unread = await unreadTask;

        if (unread.Ok)
            DreamPortShellBridge.PublishUnreadCount(unread.Unread);

        if (center.FailureReason == DreamPortFailureReason.Expired
            || titles.FailureReason == DreamPortFailureReason.Expired
            || unread.FailureReason == DreamPortFailureReason.Expired)
        {
            if (showErrorInStatus)
                HandleSessionExpired(center.Message ?? titles.Message ?? "登录已过期，请重新登录。");
            return false;
        }

        if (center.Ok)
            ApplyPointsCenter(center);
        else if (showErrorInStatus)
            SetStatus(center.Message ?? "获取积分与签到信息失败。", DreamPortHintLevel.Warning);

        if (titles.Ok)
            ApplyTitlesMine(titles);
        else if (showErrorInStatus && center.Ok)
            SetStatus(titles.Message ?? "获取称号列表失败。", DreamPortHintLevel.Warning);

        var signedNow = false;
        if (allowAutoSignin && center.Ok && !center.SignedToday && IsApprovedPlayer)
        {
            var outcome = await _autoSigninService.TryAutoSigninAsync(_api, LoggedInUsername, profileStatus, ct);
            if (outcome.SignedNow)
            {
                signedNow = true;
                var refreshedCenter = await _api.GetPointsCenterAsync(ct);
                if (refreshedCenter.Ok)
                    ApplyPointsCenter(refreshedCenter);
            }
        }

        if (showErrorInStatus && center.Ok && titles.Ok)
            SetStatus("✓ 已刷新积分、每日签到与称号状态。", DreamPortHintLevel.Info);

        return signedNow;
    }

    private void ApplyPointsCenter(DreamPortPointsCenterResult center)
    {
        PointsBalanceText = $"{center.Balance} 积分";
        IsSignedToday = center.SignedToday;
        SigninStatusText = center.SignedToday
            ? "今日已签到"
            : IsApprovedPlayer
                ? "今日尚未签到"
                : "通过入服审核后可每日签到";
        SigninStreakText = $"连续 {center.Streak} 天 · 本月累计 {center.MonthDays} 天";
        PendingTasksText = center.PendingClaimCount > 0
            ? $"{center.PendingClaimCount} 项任务奖励可领取"
            : "暂无待领取奖励";
    }

    private void ApplyTitlesMine(DreamPortTitlesMineResult titles)
    {
        var previousCode = SelectedTitleOption?.Code;
        _equippedTitleCode = titles.EquippedCode;

        OwnedTitles.Clear();
        DreamPortTitleOption? equippedMatch = null;
        DreamPortTitleOption? previousMatch = null;

        foreach (var item in titles.Owned)
        {
            var isEquipped = !string.IsNullOrEmpty(_equippedTitleCode)
                && string.Equals(item.Code, _equippedTitleCode, StringComparison.Ordinal);
            var opt = new DreamPortTitleOption(item.Code, item.Name, item.Desc, item.Color, isEquipped);
            OwnedTitles.Add(opt);
            if (isEquipped)
                equippedMatch = opt;
            if (!string.IsNullOrEmpty(previousCode) && string.Equals(item.Code, previousCode, StringComparison.Ordinal))
                previousMatch = opt;
        }

        SelectedTitleOption = equippedMatch ?? previousMatch ?? OwnedTitles.FirstOrDefault();

        var equippedLabel = equippedMatch is not null
            ? equippedMatch.Name
            : string.IsNullOrEmpty(_equippedTitleCode)
                ? "未佩戴称号"
                : _equippedTitleCode;
        var achievementSuffix = titles.TotalAchievements > 0
            ? $"（成就 {titles.CompletedAchievements}/{titles.TotalAchievements} · 未解锁 {titles.LockedCount} 个）"
            : "";
        EquippedTitleText = $"{equippedLabel}{achievementSuffix}";

        OnPropertyChanged(nameof(HasOwnedTitles));
        OnPropertyChanged(nameof(CanUnequipTitle));
    }

    /// <summary>
    /// 确保 API 客户端指向规范化后的站点地址；若切换到新站点且旧站点处于已登录状态，
    /// 会先尽力向旧站点发送 /api/logout 吊销会话并清理本地凭据与主页缓存。
    /// </summary>
    private async Task<bool> EnsureClientForBaseUrlAsync()
    {
        if (!DreamPortApiClient.TryNormalizeBaseUrl(BaseUrl, out var normalized, out var err))
        {
            SetStatus(err ?? "站点地址格式无效。", DreamPortHintLevel.Error);
            return false;
        }

        BaseUrl = normalized;
        if (!string.Equals(_api.BaseUrl, normalized, StringComparison.Ordinal))
        {
            CancelActiveLoginWait();
            CancelMsaVerifyInternal();
            ClearWaitingState();

            var oldUrl = _api.BaseUrl;
            if (!string.IsNullOrEmpty(_api.CurrentToken))
            {
                try
                {
                    await _api.LogoutAsync();
                }
                catch
                {
                    // 旧站点不可达时仍继续切换到新站点
                }
            }

            _store.ClearToken(oldUrl);
            _store.SaveBaseUrlOnly(normalized);
            IsLoggedIn = false;
            IsAdminAccount = false;
            LoggedInUsername = string.Empty;
            AccountMetaSummary = string.Empty;
            ResetAccountDerivedState();

            _api.Dispose();
            _api = _clientFactory(normalized);
            _onSessionOrSiteChanged?.Invoke();
            _onHomepagePreferenceChanged?.Invoke();
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
                sb.Append($" | 账号状态：{TranslateAccountStatus(profile.Status)}");
            if (!string.IsNullOrWhiteSpace(profile.Email))
                sb.Append($" | 邮箱：{profile.Email}");
            if (profile.Points.HasValue || profile.CheckinStreak.HasValue)
                sb.Append($"\n积分：{profile.Points?.ToString() ?? "-"} | 连续签到：{profile.CheckinStreak?.ToString() ?? "-"} 天");
        }

        AccountMetaSummary = sb.ToString();
    }

    private static string TranslateAccountStatus(string status) => status.Trim().ToLowerInvariant() switch
    {
        "approved" => "已通过白名单",
        "pending_review" => "等待管理员审核",
        "pending" => "待完成入服步骤",
        "unverified" => "待完成身份验证",
        "invited_pending" => "等待邀请人确认",
        "rejected" => "审核未通过",
        "banned" => "已封禁",
        _ => status
    };

    private void SetStatus(string message, DreamPortHintLevel level)
    {
        StatusMessage = message;
        HintLevel = level;
        IsStatusError = level == DreamPortHintLevel.Error;
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
