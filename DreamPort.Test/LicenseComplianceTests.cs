using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL;
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
            ".cs", ".axaml", ".xaml", ".csproj", ".md", ".sh", ".json", ".txt", ".yml", ".yaml", ".desktop", ".toml"
        };

        var filesToScan = new List<string>
        {
            Path.Combine(root, "README.md"),
            Path.Combine(root, "README-EN.md"),
            Path.Combine(root, "README-ZH_TW.md"),
            Path.Combine(root, "CONTRIBUTING.md"),
            Path.Combine(root, "cliff.toml"),
            Path.Combine(root, "NOTICE"),
            Path.Combine(root, "scripts", "package-linux.sh"),
            Path.Combine(root, "scripts", "package-macos.sh"),
            Path.Combine(root, "scripts", "linux", "AppRun"),
            Path.Combine(root, "scripts", "linux", "pcl-dreamport-wrapper"),
            Path.Combine(root, "scripts", "linux", "pcl-dreamport.desktop"),
            Path.Combine(root, "scripts", "linux", "pcl-dreamport-appimage.desktop"),
            Path.Combine(root, "PCL.Launcher", "Views", "Pages", "Tools", "PageToolsDreamPortAccount.axaml"),
            Path.Combine(root, "PCL.Launcher", "Views", "Pages", "Tools", "PageToolsDreamPortAccount.axaml.cs"),
            Path.Combine(root, "PCL.Launcher", "Views", "Pages", "Tools", "PageToolsGameLink.axaml"),
            Path.Combine(root, "PCL.Launcher", "Views", "Pages", "Tools", "PageToolsGameLink.axaml.cs"),
            Path.Combine(root, "PCL.Launcher", "ViewModels", "Pages", "PageToolsGameLinkViewModel.cs")
        };

        var githubDir = Path.Combine(root, ".github");
        if (Directory.Exists(githubDir))
        {
            foreach (var file in Directory.EnumerateFiles(githubDir, "*", SearchOption.AllDirectories))
            {
                if (extensions.Contains(Path.GetExtension(file)))
                    filesToScan.Add(file);
            }
        }

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

    [TestMethod]
    public void NoUpstreamCeEndpointsOrMirrorChyanLeftInLauncherCode()
    {
        var root = FindRepoRoot();

        Assert.IsFalse(
            File.Exists(Path.Combine(root, "PCL.Launcher.Core", "Updates", "UpdatesMirrorChyanModel.cs")),
            "应已移除上游 UpdatesMirrorChyanModel.cs");

        var targetFiles = new[]
        {
            Path.Combine(root, "PCL.Launcher.Core", "Updates", "UpdateManager.cs"),
            Path.Combine(root, "PCL.Launcher.Core", "Updates", "AnnouncementService.cs"),
            Path.Combine(root, "PCL.Launcher", "Views", "Pages", "Setup", "PageSetupUpdate.axaml"),
            Path.Combine(root, "PCL.Launcher", "Views", "Pages", "Setup", "PageSetupUpdate.axaml.cs"),
            Path.Combine(root, "PCL.Launcher", "ViewModels", "Setup", "PageSetupUpdateViewModel.cs"),
            Path.Combine(root, "PCL.Launcher", "Views", "Pages", "Setup", "PageSetupFeedback.axaml.cs"),
            Path.Combine(root, "PCL.Launcher", "ViewModels", "Setup", "PageSetupFeedbackViewModel.cs"),
            Path.Combine(root, "PCL.Launcher", "Views", "Pages", "Launch", "PageLaunchRight.axaml.cs"),
            Path.Combine(root, "PCL.Launcher", "Views", "Pages", "Homepage", "HomepagePresetTable.cs"),
            Path.Combine(root, "PCL.Launcher", "Views", "Pages", "Setup", "PageSetupUI.axaml.cs"),
            Path.Combine(root, "PCL.Launcher", "Views", "Pages", "Setup", "PageSetupLauncherLanguage.axaml.cs"),
            Path.Combine(root, "PCL.Launcher", "Views", "Shell", "StartupEffects.cs"),
            Path.Combine(root, "PCL.Core", "App", "Essentials", "BuildProvenance.cs"),
            Path.Combine(root, "PCL.Core", "IO", "Net", "Http", "HttpSenderExtension.cs"),
            Path.Combine(root, "PCL.Core", "IO", "Net", "Dns", "DnsQuery.cs")
        };

        var forbiddenTokens = new[]
        {
            "PCL2_CE_Server",
            "mirrorchyan.com",
            "s3.pysio.online/pcl2-ce",
            "www.pclc.cc/img/pcl-ce",
            "translate.pclc.cc",
            "doh.pysio.online",
            "PCL-Community/PCL2-CE/issues",
            "PCL-Community/PCL2-CE/releases",
            "pclcommunity"
        };

        foreach (var file in targetFiles)
        {
            Assert.IsTrue(File.Exists(file), $"缺少检查文件：{file}");
            var content = File.ReadAllText(file, Encoding.UTF8);
            foreach (var token in forbiddenTokens)
            {
                Assert.IsFalse(
                    content.Contains(token, StringComparison.OrdinalIgnoreCase),
                    $"文件 {Path.GetRelativePath(root, file)} 仍残留上游标识：{token}");
            }
        }

        Assert.IsFalse(UpdateManager.HasConfiguredUpdateSource);
        Assert.AreEqual(UpdateEnums.VersionStatus.Latest, UpdateManager.GetVersionStatus());

        Assert.AreEqual(
            "https://api.github.com/repos/Xiameng/PCL-DreamPort/issues?state=all&sort=created&per_page=200",
            PageSetupFeedbackViewModel.BuildIssuesApiUrl("https://github.com/Xiameng/PCL-DreamPort"));
        Assert.AreEqual(
            "https://api.github.com/repos/Owner/CustomRepo/issues?state=all&sort=created&per_page=200",
            PageSetupFeedbackViewModel.BuildIssuesApiUrl("https://github.com/Owner/CustomRepo.git/"));
        Assert.AreEqual(
            "https://api.github.com/repos/Xiameng/PCL-DreamPort/issues?state=all&sort=created&per_page=200",
            PageSetupFeedbackViewModel.BuildIssuesApiUrl("https://git.xmcraft.cn/Xiameng/PCL-DreamPort"));
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
