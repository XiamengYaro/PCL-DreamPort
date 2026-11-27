using System.Reflection;
using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Navigation;
using PCL.UI.Controls;
using PCL.UI.Xaml;

namespace DreamPort.Test;

[TestClass]
public sealed class HomepageXamlParsingTests
{
    private static Thickness? ParseThickness(string? raw)
    {
        var method = typeof(RuntimeXamlRenderer).GetMethod(
            "ParseThickness",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method, "未找到 RuntimeXamlRenderer.ParseThickness");
        return (Thickness?)method.Invoke(null, [raw]);
    }

    [TestMethod]
    public void ParseThickness_SupportsUniformTwoValueAndFourValueForms()
    {
        Assert.AreEqual(new Thickness(8), ParseThickness("8"));
        // 服务端下发的主页按钮使用 Padding="13,6"（水平, 垂直），此前被忽略导致按钮过小
        Assert.AreEqual(new Thickness(13, 6), ParseThickness("13,6"));
        Assert.AreEqual(new Thickness(0, 0, 10, 0), ParseThickness("0,0,10,0"));
        Assert.IsNull(ParseThickness("1,2,3"));
        Assert.IsNull(ParseThickness("a,b"));
        Assert.IsNull(ParseThickness(""));
    }

    [TestMethod]
    public void BackendHomepageImages_RenderAsMyImageAndImageBrushBackground()
    {
        const string xaml = """
            <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:local="clr-namespace:PCL;assembly=Plain Craft Launcher 2">
                <Border Height="240" CornerRadius="8" ClipToBounds="True" Margin="0,0,0,16">
                    <Border.Background>
                        <ImageBrush ImageSource="https://xmcraft.cn/uploads/hero.webp" Stretch="UniformToFill" />
                    </Border.Background>
                    <Grid>
                        <Border Background="#70000000" />
                        <StackPanel Margin="24" VerticalAlignment="Bottom">
                            <TextBlock FontSize="22" FontWeight="Bold" Foreground="#FFFFFFFF"
                                       TextWrapping="Wrap" Text="港湾实景" />
                            <TextBlock Foreground="#E6FFFFFF" TextWrapping="Wrap" Text="从梦港出发" />
                        </StackPanel>
                    </Grid>
                </Border>
                <local:MyCard Title="照片墙" Margin="0,0,0,15" CanSwap="True">
                    <StackPanel Margin="25,36,23,20">
                        <WrapPanel Orientation="Horizontal" ItemWidth="270" Margin="0,0,0,10">
                            <StackPanel Width="260" Margin="0,0,10,10">
                                <local:MyImage Source="https://xmcraft.cn/uploads/photo.webp"
                                               Height="132" Stretch="UniformToFill" CornerRadius="6" />
                                <TextBlock TextWrapping="Wrap" Margin="0,10,0,0" Text="港湾日落" />
                            </StackPanel>
                        </WrapPanel>
                    </StackPanel>
                </local:MyCard>
            </StackPanel>
            """;

        var result = RuntimeXamlRenderer.RenderFragment(xaml, _ => null);

        Assert.IsTrue(result.Success, result.Error ?? string.Join("; ", result.Warnings));
        var root = result.Controls.Single() as StackPanel;
        Assert.IsNotNull(root);
        var border = root.Children.OfType<Border>().Single();
        Assert.AreEqual(240, border.Height);
        Assert.AreEqual(new CornerRadius(8), border.CornerRadius);
        var backgroundGrid = border.Child as Grid;
        Assert.IsNotNull(backgroundGrid);
        var hero = backgroundGrid.Children.OfType<MyImage>().Single();
        Assert.AreEqual("https://xmcraft.cn/uploads/hero.webp", hero.Source);
        Assert.AreEqual(Stretch.UniformToFill, hero.Stretch);
        var heroContent = FindControls<StackPanel>(border)
            .Single(panel => panel.Children.OfType<TextBlock>().Any());
        Assert.AreEqual(2, heroContent.Children.OfType<TextBlock>().Count());
        Assert.IsFalse(FindControls<MyButton>(border).Any());

        var card = root.Children.OfType<MyCard>().Single();
        var wrap = FindControls<WrapPanel>(card).Single();
        Assert.AreEqual(270, wrap.ItemWidth);
        var tile = wrap.Children.OfType<StackPanel>().Single();
        Assert.AreEqual(260, tile.Width);
        var image = tile.Children.OfType<MyImage>().Single();
        Assert.AreEqual("https://xmcraft.cn/uploads/photo.webp", image.Source);
        Assert.AreEqual(new CornerRadius(6), image.CornerRadius);
    }

    [TestMethod]
    public void PageRouterFocusAndLaunchRightScroll_SuppressInitialBringIntoViewAndResetToTop()
    {
        var scrollViewer = new MyScrollViewer();
        Assert.IsFalse(scrollViewer.IsSuppressingBringIntoView);
        scrollViewer.BeginSuppressBringIntoView();
        scrollViewer.BeginSuppressBringIntoView();
        Assert.IsTrue(scrollViewer.IsSuppressingBringIntoView);
        scrollViewer.EndSuppressBringIntoView();
        Assert.IsTrue(scrollViewer.IsSuppressingBringIntoView, "嵌套抑制退出一层时应继续保持抑制");
        scrollViewer.EndSuppressBringIntoView();
        Assert.IsFalse(scrollViewer.IsSuppressingBringIntoView);
        scrollViewer.EndSuppressBringIntoView();
        Assert.IsFalse(scrollViewer.IsSuppressingBringIntoView);
        scrollViewer.ResetToTop();
        Assert.AreEqual(0d, scrollViewer.Offset.Y);

        var button = new MyButton { Text = "测试按钮" };
        scrollViewer.Content = button;
        PageRouter.FocusControlWithoutScrolling(button);
        Assert.IsTrue(ScrollViewer.GetBringIntoViewOnFocusChange(button), "聚焦完成后应恢复控件原有 BringIntoViewOnFocusChange");
        Assert.IsFalse(scrollViewer.IsSuppressingBringIntoView);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PCL Community Edition.slnx")))
            dir = dir.Parent;
        Assert.IsNotNull(dir);

        var pageRouterCs = File.ReadAllText(Path.Combine(dir.FullName, "PCL.Launcher", "Navigation", "PageRouter.cs"));
        Assert.IsTrue(pageRouterCs.Contains("FocusControlWithoutScrolling(target)", StringComparison.Ordinal));
        Assert.IsTrue(pageRouterCs.Contains("BeginSuppressBringIntoView", StringComparison.Ordinal));

        var scrollViewerCs = File.ReadAllText(Path.Combine(dir.FullName, "PCL.Launcher", "UI", "Controls", "Containers", "MyScrollViewer.cs"));
        Assert.IsTrue(scrollViewerCs.Contains("IsSuppressingBringIntoView", StringComparison.Ordinal));

        var launchRightCs = File.ReadAllText(Path.Combine(dir.FullName, "PCL.Launcher", "Views", "Pages", "Launch", "PageLaunchRight.axaml.cs"));
        Assert.IsTrue(launchRightCs.Contains("ResetScrollToTop()", StringComparison.Ordinal));
        Assert.IsTrue(launchRightCs.Contains("wasAtTop", StringComparison.Ordinal));
    }

    private static IEnumerable<T> FindControls<T>(Control root)
        where T : Control
    {
        if (root is T match)
            yield return match;
        foreach (var child in root.GetVisualChildren().OfType<Control>())
            foreach (var nested in FindControls<T>(child))
                yield return nested;
    }
}
