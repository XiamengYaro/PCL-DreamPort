using System.Reflection;
using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
                <local:MyCard Title="港湾实景" Margin="0,0,0,15">
                    <Border CornerRadius="6" ClipToBounds="True" Margin="25,25,23,10" MinHeight="180">
                        <Border.Background>
                            <ImageBrush ImageSource="https://xmcraft.cn/uploads/hero.webp" Stretch="UniformToFill" />
                        </Border.Background>
                    </Border>
                    <StackPanel Margin="25,25,23,15">
                        <WrapPanel Orientation="Horizontal" ItemWidth="220">
                            <StackPanel Width="210" Margin="0,0,10,10">
                                <local:MyImage Source="https://xmcraft.cn/uploads/photo.webp"
                                               Height="132" Stretch="UniformToFill" CornerRadius="6" />
                                <TextBlock TextWrapping="Wrap" Text="港湾日落" />
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
        var card = root.Children.OfType<MyCard>().Single();
        var border = FindControls<Border>(card).FirstOrDefault(b => b.Child is Grid);
        Assert.IsNotNull(border);
        var backgroundGrid = border.Child as Grid;
        Assert.IsNotNull(backgroundGrid);
        var hero = backgroundGrid.Children.OfType<MyImage>().Single();
        Assert.AreEqual("https://xmcraft.cn/uploads/hero.webp", hero.Source);
        Assert.AreEqual(Stretch.UniformToFill, hero.Stretch);

        var wrap = FindControls<WrapPanel>(card).Single();
        var tile = wrap.Children.OfType<StackPanel>().Single();
        var image = tile.Children.OfType<MyImage>().Single();
        Assert.AreEqual("https://xmcraft.cn/uploads/photo.webp", image.Source);
        Assert.AreEqual(new CornerRadius(6), image.CornerRadius);
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
