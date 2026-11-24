using DreamPort.Core.Homepage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Xaml;

namespace DreamPort.Test;

[TestClass]
public sealed class HomepagePrototypeTests
{
    [TestMethod]
    public void PrototypeXaml_PassesFormatDetectorAndSecurityValidator_AndHasNoColorEmoji()
    {
        var xaml = DreamPortHomepageService.GetPrototypeXaml();

        Assert.IsFalse(string.IsNullOrWhiteSpace(xaml));
        Assert.IsTrue(HomepageXamlFormatDetector.IsXaml(xaml), "原型内容应被识别为合法 XAML。");

        var result = XamlSecurityValidator.ValidateFragment(xaml);
        Assert.IsTrue(
            result.IsValid,
            "原型 XAML 应通过 XamlSecurityValidator 安全校验：" + string.Join("; ", result.Errors));

        Assert.IsFalse(
            LicenseComplianceTests.ContainsColorEmoji(xaml, out var offense),
            $"主页原型 XAML 不应包含彩色 emoji：{offense}");
    }

    [TestMethod]
    public void RemoteHomepageUrl_NormalizesBaseUrl()
    {
        Assert.AreEqual(
            "https://xmcraft.cn/api/client/pcl-homepage",
            DreamPortHomepageService.GetRemoteHomepageUrl("https://xmcraft.cn/"));
        Assert.AreEqual(
            "https://dp.example.com:8443/api/client/pcl-homepage",
            DreamPortHomepageService.GetRemoteHomepageUrl("  https://dp.example.com:8443///  "));
        Assert.AreEqual(
            "https://xmcraft.cn/api/client/pcl-homepage/version",
            DreamPortHomepageService.GetRemoteVersionUrl("https://xmcraft.cn/"));
    }

    [TestMethod]
    public void BackendTranslatedPclXaml_PassesFormatDetectorAndSecurityValidator()
    {
        const string sampleBackendXaml = """
            <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        xmlns:local="clr-namespace:PCL;assembly=Plain Craft Launcher 2">
                <local:MyHint Margin="0,0,0,15" IsWarn="False" Text="夏日小镇 · 港湾主世界" />
                <local:MyCard Title="灯塔与夏日港湾已就绪" Margin="0,0,0,15" CanSwap="True">
                    <StackPanel Margin="25,40,23,15">
                        <TextBlock TextWrapping="Wrap" Margin="0,0,0,10" Text="主城生存服 &amp; &quot;机械创造服&quot; 开放中" />
                        <StackPanel Orientation="Horizontal">
                            <local:MyButton Padding="13,6" Margin="0,0,10,0" ColorType="Highlight"
                                            Text="打开梦港门户" EventType="打开网页" EventData="https://xmcraft.cn" />
                            <local:MyButton Padding="13,6" Margin="0,0,10,0"
                                            Text="刷新主页" EventType="刷新主页" />
                        </StackPanel>
                    </StackPanel>
                </local:MyCard>
            </StackPanel>
            """;

        Assert.IsTrue(HomepageXamlFormatDetector.IsXaml(sampleBackendXaml));
        var result = XamlSecurityValidator.ValidateFragment(sampleBackendXaml);
        Assert.IsTrue(result.IsValid, string.Join("; ", result.Errors));
    }
}
