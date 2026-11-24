using System.ComponentModel;
using Avalonia.Interactivity;
using DreamPort.UI.ViewModels;
using PCL.Navigation;
using PCL.UI.Controls;

namespace PCL.Views.Pages.Tools;

/// <summary>工具页中的梦港联机大厅页（仅梦港 TCP/TLS 中继与局域网广播）。</summary>
public partial class PageToolsGameLink : MyPageRight
{
    public PageToolsGameLink()
    {
        InitializeComponent();
        PanScroll = PanBack;
        Current = this;
        DataContext = ViewModel;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateHintTheme();
        Loaded += OnPageLoaded;
    }

    public static PageToolsGameLink? Current { get; private set; }

    public PageToolsGameLinkViewModel ViewModel { get; } = new();

    public void Reload()
    {
        ViewModel.Reload();
    }

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
        if (e.PropertyName == nameof(DreamPortLobbyViewModel.HintLevel))
            UpdateHintTheme();
    }

    private void UpdateHintTheme()
    {
        HintStatus.Theme = ViewModel.HintLevel switch
        {
            DreamPortHintLevel.Error => MyHint.Themes.Red,
            DreamPortHintLevel.Warning => MyHint.Themes.Yellow,
            _ => MyHint.Themes.Blue
        };
    }
}
