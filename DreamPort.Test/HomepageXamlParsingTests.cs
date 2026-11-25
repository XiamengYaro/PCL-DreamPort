using System.Reflection;
using Avalonia;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
}
