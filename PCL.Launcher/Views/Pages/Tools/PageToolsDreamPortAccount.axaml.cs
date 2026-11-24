using System.ComponentModel;
using Avalonia.Interactivity;
using DreamPort.UI.ViewModels;
using PCL.UI.Controls;
using PCL.Views.Pages.Launch;

namespace PCL.Views.Pages.Tools;

/// <summary>工具页中的 DreamPort 账号管理与主页原型联调入口页（原生 MyPageRight + MyCard 结构）。</summary>
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
        onHomepagePreferenceChanged: () => PageLaunchRight.Current?.ForceRefresh());

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
        if (e.PropertyName == nameof(DreamPortAccountViewModel.IsStatusError))
        {
            HintStatus.Theme = ViewModel.IsStatusError
                ? MyHint.Themes.Red
                : MyHint.Themes.Blue;
        }
    }
}
