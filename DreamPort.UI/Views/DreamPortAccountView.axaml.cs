using Avalonia.Controls;
using DreamPort.UI.ViewModels;

namespace DreamPort.UI.Views;

/// <summary>DreamPort 账号登录与自定义主页联调视图。</summary>
public partial class DreamPortAccountView : UserControl
{
    public DreamPortAccountView()
    {
        InitializeComponent();
    }

    public DreamPortAccountView(DreamPortAccountViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
