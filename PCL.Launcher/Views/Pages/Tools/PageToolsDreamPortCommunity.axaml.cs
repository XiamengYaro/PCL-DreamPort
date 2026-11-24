using System.ComponentModel;
using Avalonia.Interactivity;
using DreamPort.UI.ViewModels;
using PCL.Navigation;
using PCL.UI.Controls;

namespace PCL.Views.Pages.Tools;

/// <summary>工具页中的梦港服务器公告与站内通知中心（原生 MyPageRight + MyCard 结构）。</summary>
public partial class PageToolsDreamPortCommunity : MyPageRight
{
    public PageToolsDreamPortCommunity()
    {
        InitializeComponent();
        PanScroll = PanBack;
        DataContext = ViewModel;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnPageLoaded;
    }

    public DreamPortCommunityViewModel ViewModel { get; } = new(
        requestGoToAccount: () => PageRouter.Navigate(PageIds.Tools, SubPageIds.ToolsDreamPortAccount));

    public override async void OnNavigatedTo(PageParameter? parameter)
    {
        base.OnNavigatedTo(parameter);
        await SafeSyncAsync();
    }

    private async void OnPageLoaded(object? sender, RoutedEventArgs e)
    {
        await SafeSyncAsync();
    }

    private async Task SafeSyncAsync()
    {
        try
        {
            await ViewModel.InitializeOrSyncAsync();
        }
        catch
        {
            // ViewModel 内部已处理网络与会话状态异常
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DreamPortCommunityViewModel.HintLevel)
            or nameof(DreamPortCommunityViewModel.IsStatusError))
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
