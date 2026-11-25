using System.ComponentModel;
using Avalonia.Interactivity;
using DreamPort.UI.ViewModels;
using PCL.Navigation;
using PCL.UI.Controls;

namespace PCL.Views.Pages.Tools;

/// <summary>工具页中的梦港「同步与工具」页面（原生 MyPageRight + MyCard 结构）。</summary>
public partial class PageToolsDreamPortSync : MyPageRight
{
    public PageToolsDreamPortSync()
    {
        InitializeComponent();
        PanScroll = PanBack;
        DataContext = ViewModel;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateHintTheme();
        Loaded += OnPageLoaded;
    }

    public PageToolsDreamPortSyncViewModel ViewModel { get; } = new();

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
            // ViewModel 内部已处理网络与实例状态异常
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DreamPortSyncViewModel.HintLevel))
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
