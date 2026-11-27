using System.ComponentModel;
using Avalonia.Interactivity;
using DreamPort.UI.ViewModels;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Authentication;
using PCL.Core.Minecraft.Profile.Models;
using PCL.UI.Controls;
using PCL.Views.Pages.Homepage;
using PCL.Views.Pages.Launch;

namespace PCL.Views.Pages.Tools;

/// <summary>工具页中的 DreamPort 账号、多设备会话与六步入服引导页面（原生 MyPageRight + MyCard 结构）。</summary>
public partial class PageToolsDreamPortAccount : MyPageRight
{
    private bool _loadedOnce;

    public PageToolsDreamPortAccount()
    {
        InitializeComponent();
        PanScroll = PanBack;
        DataContext = ViewModel;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnPageLoaded;
    }

    public DreamPortAccountViewModel ViewModel { get; } = new(
        onHomepagePreferenceChanged: () =>
        {
            HomepageSource.ClearCache();
            PageLaunchRight.Current?.ForceRefresh();
        },
        acquireMcToken: AcquireActiveMicrosoftTokenAsync,
        getActiveMcProfileName: GetActiveMicrosoftProfileName);

    private static string? GetActiveMicrosoftProfileName()
    {
        var current = ProfileService.Current;
        if (current is { ProfileType: ProfileType.Microsoft })
            return current.UserName;

        return ProfileService.Profiles
            .FirstOrDefault(p => p.ProfileType == ProfileType.Microsoft)
            ?.UserName;
    }

    private static async Task<(string? AccessToken, string? PlayerName, string? Error)> AcquireActiveMicrosoftTokenAsync(
        CancellationToken ct)
    {
        try
        {
            var existing = ProfileService.Current is { ProfileType: ProfileType.Microsoft } current
                ? current
                : ProfileService.Profiles.FirstOrDefault(p => p.ProfileType == ProfileType.Microsoft);

            if (existing is null)
                return (null, null, "尚未在启动器登录微软正版账号，请先前往「启动」页完成微软登录。");

            if (!string.IsNullOrWhiteSpace(existing.AccessToken) && !existing.IsExpired)
                return (existing.AccessToken, existing.UserName, null);

            var refreshed = await ProfileService.AuthenticateAsync(
                ProfileType.Microsoft,
                new AuthenticationRequest
                {
                    ForceRefresh = false,
                    DeviceCodeHandler = LoginProfile.ShowDeviceCodeLoginAsync
                },
                existing,
                true,
                ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(refreshed.AccessToken))
                return (null, refreshed.UserName, "获取微软 Minecraft 访问令牌失败，请尝试重新登录微软账号。");

            return (refreshed.AccessToken, refreshed.UserName, null);
        }
        catch (OperationCanceledException)
        {
            return (null, null, "已取消微软正版令牌获取。");
        }
        catch (Exception ex)
        {
            return (null, null, $"读取或刷新微软正版令牌失败：{ex.Message}");
        }
    }

    private async void OnPageLoaded(object? sender, RoutedEventArgs e)
    {
        if (_loadedOnce)
            return;
        _loadedOnce = true;

        try
        {
            await ViewModel.InitializeAsync();
        }
        catch
        {
            // InitializeAsync 内部已处理网络与加密存储异常
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DreamPortAccountViewModel.HintLevel)
            or nameof(DreamPortAccountViewModel.IsStatusError))
        {
            HintStatus.Theme = ViewModel.HintLevel switch
            {
                DreamPortHintLevel.Error => MyHint.Themes.Red,
                DreamPortHintLevel.Warning => MyHint.Themes.Yellow,
                _ => MyHint.Themes.Blue
            };
        }
    }
}
