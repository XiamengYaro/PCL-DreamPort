using Avalonia.Interactivity;
using PCL.Navigation;
using PCL.UI.Controls;

namespace PCL.Views.Pages.Tools;

/// <summary>工具页中的梦港联机大厅页（仅梦港 TCP/TLS 中继）。</summary>
public partial class PageToolsGameLink : MyPageRight
{
    private bool _loadedOnce;

    public PageToolsGameLink()
    {
        InitializeComponent();
        PanScroll = PanBack;
        Current = this;
        DataContext = ViewModel;

        BtnGoDreamPortAccount.Click += (_, _) =>
            Navigation.GoTo(new PageLocation(PageIds.Tools, SubPageIds.ToolsDreamPortAccount));

        Loaded += OnPageLoaded;
    }

    public static PageToolsGameLink? Current { get; private set; }

    public PageToolsGameLinkViewModel ViewModel { get; } = new();

    public void Reload()
    {
        ViewModel.Reload();
    }

    private void OnPageLoaded(object? sender, RoutedEventArgs e)
    {
        if (_loadedOnce)
            return;
        _loadedOnce = true;
        ViewModel.Reload();
    }
}
