using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DreamPort.UI.ViewModels;
using DreamPort.UI.Views;
using PCL.UI.Controls;
using PCL.Views.Pages.Launch;

namespace PCL.Views.Pages.Tools;

/// <summary>工具页中的 DreamPort 账号管理与主页原型联调入口页。</summary>
public sealed class PageToolsDreamPortAccount : MyPageRight
{
    private readonly DreamPortAccountViewModel _viewModel;
    private bool _loadedOnce;

    public PageToolsDreamPortAccount()
    {
        _viewModel = new DreamPortAccountViewModel(
            onHomepagePreferenceChanged: () => PageLaunchRight.Current?.ForceRefresh());

        var view = new DreamPortAccountView(_viewModel);
        var container = new StackPanel
        {
            Margin = new Thickness(25, 10)
        };
        container.Children.Add(view);

        var scrollViewer = new MyScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = container
        };

        PanScroll = scrollViewer;
        Child = scrollViewer;
        DataContext = _viewModel;

        Loaded += OnPageLoaded;
    }

    private async void OnPageLoaded(object? sender, RoutedEventArgs e)
    {
        if (_loadedOnce)
            return;
        _loadedOnce = true;

        try
        {
            await _viewModel.InitializeAsync();
        }
        catch
        {
            // InitializeAsync 内部已捕获网络/存储异常，此处仅防止 async void 逃逸
        }
    }
}
