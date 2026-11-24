using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App;

namespace DreamPort.Test;

[TestClass]
public sealed class LicenseComplianceTests
{
    [TestMethod]
    public void RepositoryLicenseAndNoticeFiles_ExistAndPreserveAttribution()
    {
        var root = FindRepoRoot();

        var license = ReadRequiredFile(root, "LICENSE");
        var launcherLicence = ReadRequiredFile(root, "PCL.Launcher", "LICENCE");
        var launcherCoreLicence = ReadRequiredFile(root, "PCL.Launcher.Core", "LICENCE");
        var notice = ReadRequiredFile(root, "NOTICE");
        var readme = ReadRequiredFile(root, "README.md");

        Assert.IsTrue(license.Contains("Apache License", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(launcherLicence.Length > 200);
        Assert.IsTrue(launcherCoreLicence.Length > 200);

        Assert.IsTrue(notice.Contains("独立二次创作", StringComparison.Ordinal));
        Assert.IsTrue(notice.Contains("与龙腾猫跃及 PCL 官方无关联", StringComparison.Ordinal));
        Assert.IsTrue(notice.Contains("Apache License 2.0", StringComparison.OrdinalIgnoreCase));

        Assert.IsTrue(readme.Contains("独立二次创作", StringComparison.Ordinal));
        Assert.IsTrue(readme.Contains("与龙腾猫跃及 PCL 官方无关联", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ColorThemes_RemainLockedToFourOriginalBlueHues()
    {
        var values = Enum.GetValues<ColorTheme>();
        Assert.HasCount(4, values);
        CollectionAssert.AreEquivalent(
            new[]
            {
                ColorTheme.SkyBlue,
                ColorTheme.CatBlue,
                ColorTheme.DeathBlue,
                ColorTheme.HmclBlue
            },
            values);
    }

    [TestMethod]
    public void AboutPage_WindowTitle_AndLaunchPrecheck_MeetPclLicenseRequirements()
    {
        var root = FindRepoRoot();

        var mainWindow = ReadRequiredFile(root, "PCL.Launcher", "Views", "MainWindow.axaml");
        Assert.IsTrue(mainWindow.Contains("Title=\"PCL-梦港\"", StringComparison.Ordinal));
        Assert.IsFalse(mainWindow.Contains("修改版", StringComparison.Ordinal));

        var aboutAxaml = ReadRequiredFile(root, "PCL.Launcher", "Views", "Pages", "Setup", "PageSetupAbout.axaml");
        Assert.IsTrue(aboutAxaml.Contains("Grid.Row=\"0\"", StringComparison.Ordinal));
        Assert.IsTrue(aboutAxaml.Contains("Setup.About.OriginalAuthor.Name", StringComparison.Ordinal));
        Assert.IsTrue(aboutAxaml.Contains("BtnSponsorOriginalAuthor", StringComparison.Ordinal));

        var aboutCs = ReadRequiredFile(root, "PCL.Launcher", "Views", "Pages", "Setup", "PageSetupAbout.axaml.cs");
        Assert.IsTrue(aboutCs.Contains("https://ifdian.net/a/LTCat", StringComparison.Ordinal));
        Assert.IsTrue(aboutCs.Contains("独立二次创作", StringComparison.Ordinal));

        var launchCore = ReadRequiredFile(root, "PCL.Launcher.Core", "Launch", "Core.cs");
        Assert.IsTrue(launchCore.Contains("McLaunchPrecheck", StringComparison.Ordinal));
        Assert.IsTrue(launchCore.Contains("_ShowDonateHint()", StringComparison.Ordinal));
        Assert.IsTrue(launchCore.Contains("_ShowPurchaseHint()", StringComparison.Ordinal));
        Assert.IsFalse(launchCore.Contains("#if BETA", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DreamPortFilesAndDocs_ContainNoColorEmoji()
    {
        var root = FindRepoRoot();
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".axaml", ".xaml", ".csproj", ".md", ".sh", ".json", ".txt"
        };

        var filesToScan = new List<string>
        {
            Path.Combine(root, "README.md"),
            Path.Combine(root, "NOTICE"),
            Path.Combine(root, "PCL.Launcher", "Views", "Pages", "Tools", "PageToolsDreamPortAccount.axaml"),
            Path.Combine(root, "PCL.Launcher", "Views", "Pages", "Tools", "PageToolsDreamPortAccount.axaml.cs"),
            Path.Combine(root, "PCL.Launcher", "Views", "Pages", "Tools", "PageToolsGameLink.axaml"),
            Path.Combine(root, "PCL.Launcher", "Views", "Pages", "Tools", "PageToolsGameLink.axaml.cs"),
            Path.Combine(root, "PCL.Launcher", "ViewModels", "Pages", "PageToolsGameLinkViewModel.cs")
        };

        foreach (var dirName in new[] { "DreamPort.Core", "DreamPort.UI", "DreamPort.Test" })
        {
            var dir = Path.Combine(root, dirName);
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dir, file);
                if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(seg => seg is "bin" or "obj"))
                {
                    continue;
                }

                if (extensions.Contains(Path.GetExtension(file)))
                    filesToScan.Add(file);
            }
        }

        foreach (var file in filesToScan)
        {
            var content = File.ReadAllText(file, Encoding.UTF8);
            Assert.IsFalse(
                ContainsColorEmoji(content, out var offense),
                $"文件 {Path.GetRelativePath(root, file)} 包含禁止的彩色 emoji：{offense}");
        }
    }

    [TestMethod]
    public void NoEasyTierOrNatayarkLeftInSource()
    {
        var root = FindRepoRoot();
        var forbiddenTokens = new[] { "Natayark", "naids.com", "EasyTier", "Scaffolding" };
        var projects = new[] { "PCL.Core", "PCL.Launcher.Core", "PCL.Launcher", "DreamPort.Core", "DreamPort.UI" };

        foreach (var project in projects)
        {
            var dir = Path.Combine(root, project);
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dir, file);
                if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(seg => seg is "bin" or "obj"))
                {
                    continue;
                }

                var ext = Path.GetExtension(file);
                if (!ext.Equals(".cs", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".axaml", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var content = File.ReadAllText(file, Encoding.UTF8);
                foreach (var token in forbiddenTokens)
                {
                    Assert.IsFalse(
                        content.Contains(token, StringComparison.OrdinalIgnoreCase),
                        $"源码文件 {Path.GetRelativePath(root, file)} 仍残留禁用标识 {token}");
                }
            }
        }
    }

    internal static bool ContainsColorEmoji(string text, out string offense)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var cp = rune.Value;
            var isForbidden = cp is (>= 0x1F300 and <= 0x1FAFF)
                or (>= 0x1F1E6 and <= 0x1F1FF)
                or 0xFE0F
                or 0x200D
                or (>= 0x2600 and <= 0x26FF)
                or (>= 0x231A and <= 0x23FA)
                or (>= 0x2700 and <= 0x27BF and not 0x2713 and not 0x2717)
                or 0x2B50
                or 0x2B55;

            if (isForbidden)
            {
                offense = $"U+{cp:X4}";
                return true;
            }
        }

        offense = string.Empty;
        return false;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCL Community Edition.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("未找到 PCL Community Edition.slnx 所在仓库根目录。");
    }

    private static string ReadRequiredFile(string root, params string[] segments)
    {
        var path = Path.Combine([root, .. segments]);
        Assert.IsTrue(File.Exists(path), $"缺少必要文件：{path}");
        var content = File.ReadAllText(path, Encoding.UTF8);
        Assert.IsFalse(string.IsNullOrWhiteSpace(content), $"文件内容为空：{path}");
        return content;
    }
}
