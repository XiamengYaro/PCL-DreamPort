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
    }
}
