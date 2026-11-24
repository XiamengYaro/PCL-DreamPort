using DreamPort.Core.Config;
using DreamPort.Core.Mirror;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DreamPort.Test;

[TestClass]
public sealed class DreamPortMirrorPolicyTests
{
    private const string SelfMirror = "https://dl.xmcraft.cn";

    [TestMethod]
    public void RewriteToMirror_CoversAllMirrorRulesAndSelfOnlyIsolation()
    {
        var cases = new (string Official, string ExpectedSelf, string ExpectedBmclapi)[]
        {
            (
                "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json",
                "https://dl.xmcraft.cn/mc/game/version_manifest_v2.json",
                "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json"
            ),
            (
                "https://launcher.mojang.com/v1/objects/abc/client.jar",
                "https://dl.xmcraft.cn/v1/objects/abc/client.jar",
                "https://bmclapi2.bangbang93.com/v1/objects/abc/client.jar"
            ),
            (
                "https://piston-meta.mojang.com/v1/packages/abc/1.21.json",
                "https://dl.xmcraft.cn/v1/packages/abc/1.21.json",
                "https://bmclapi2.bangbang93.com/v1/packages/abc/1.21.json"
            ),
            (
                "https://piston-data.mojang.com/v1/objects/def/server.jar",
                "https://dl.xmcraft.cn/v1/objects/def/server.jar",
                "https://bmclapi2.bangbang93.com/v1/objects/def/server.jar"
            ),
            (
                "https://libraries.minecraft.net/com/mojang/authlib/6.0/authlib-6.0.jar",
                "https://dl.xmcraft.cn/maven/com/mojang/authlib/6.0/authlib-6.0.jar",
                "https://bmclapi2.bangbang93.com/maven/com/mojang/authlib/6.0/authlib-6.0.jar"
            ),
            (
                "https://resources.download.minecraft.net/ab/ab1234",
                "https://dl.xmcraft.cn/assets/ab/ab1234",
                "https://bmclapi2.bangbang93.com/assets/ab/ab1234"
            ),
            (
                "https://files.minecraftforge.net/maven/net/minecraftforge/forge/1.20.1/forge.jar",
                "https://dl.xmcraft.cn/maven/net/minecraftforge/forge/1.20.1/forge.jar",
                "https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/1.20.1/forge.jar"
            ),
            (
                "https://maven.minecraftforge.net/net/minecraftforge/forge/1.21/forge.jar",
                "https://dl.xmcraft.cn/maven/net/minecraftforge/forge/1.21/forge.jar",
                "https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/1.21/forge.jar"
            ),
            (
                "https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1/neoforge-21.1.jar",
                "https://dl.xmcraft.cn/maven/net/neoforged/neoforge/21.1/neoforge-21.1.jar",
                "https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/21.1/neoforge-21.1.jar"
            ),
            (
                "https://meta.fabricmc.net/v2/versions/loader",
                "https://dl.xmcraft.cn/fabric-meta/v2/versions/loader",
                "https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader"
            ),
            (
                "https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.16.0/fabric-loader-0.16.0.jar",
                "https://dl.xmcraft.cn/fabric-maven/net/fabricmc/fabric-loader/0.16.0/fabric-loader-0.16.0.jar",
                "https://bmclapi2.bangbang93.com/maven/net/fabricmc/fabric-loader/0.16.0/fabric-loader-0.16.0.jar"
            ),
            (
                "https://meta.quiltmc.org/v3/versions/loader",
                "https://dl.xmcraft.cn/quilt-meta/v3/versions/loader",
                "https://meta.quiltmc.org/v3/versions/loader"
            ),
            (
                "https://maven.quiltmc.org/repository/release/org/quiltmc/quilt-loader/0.26/quilt.jar",
                "https://dl.xmcraft.cn/maven/org/quiltmc/quilt-loader/0.26/quilt.jar",
                "https://bmclapi2.bangbang93.com/maven/org/quiltmc/quilt-loader/0.26/quilt.jar"
            ),
            (
                "https://meta.legacyfabric.net/v2/versions/loader",
                "https://dl.xmcraft.cn/legacyfabric-meta/v2/versions/loader",
                "https://meta.legacyfabric.net/v2/versions/loader"
            ),
            (
                "https://maven.legacyfabric.net/net/legacyfabric/loader/1.8.9/loader.jar",
                "https://dl.xmcraft.cn/legacyfabric-maven/net/legacyfabric/loader/1.8.9/loader.jar",
                "https://maven.legacyfabric.net/net/legacyfabric/loader/1.8.9/loader.jar"
            ),
            (
                "https://dl.liteloader.com/versions/versions.json",
                "https://dl.xmcraft.cn/liteloader/versions/versions.json",
                "https://dl.liteloader.com/versions/versions.json"
            ),
            (
                "https://maven.cleanroommc.com/com/cleanroommc/cleanroom/0.2.0/cleanroom.jar",
                "https://dl.xmcraft.cn/cleanroom-maven/com/cleanroommc/cleanroom/0.2.0/cleanroom.jar",
                "https://maven.cleanroommc.com/com/cleanroommc/cleanroom/0.2.0/cleanroom.jar"
            ),
            (
                "https://repo.spongepowered.org/maven/org/spongepowered/mixin/0.8/mixin.jar",
                "https://dl.xmcraft.cn/maven/org/spongepowered/mixin/0.8/mixin.jar",
                "https://bmclapi2.bangbang93.com/maven/org/spongepowered/mixin/0.8/mixin.jar"
            ),
            (
                "https://laby-releases.s3.de.io.cloud.ovh.net/api/v1/manifest.json",
                "https://dl.xmcraft.cn/labymod/api/v1/manifest.json",
                "https://laby-releases.s3.de.io.cloud.ovh.net/api/v1/manifest.json"
            ),
            (
                "https://releases.r2.labymod.net/api/v1/index.json",
                "https://dl.xmcraft.cn/labymod-api/v1/index.json",
                "https://releases.r2.labymod.net/api/v1/index.json"
            ),
            (
                "https://releases.r2.labymod.net/libraries/org/example/lib.jar",
                "https://dl.xmcraft.cn/labymod-maven/org/example/lib.jar",
                "https://releases.r2.labymod.net/libraries/org/example/lib.jar"
            )
        };

        foreach (var item in cases)
        {
            Assert.AreEqual(item.ExpectedSelf, DreamPortMirrorPolicy.RewriteToMirror(item.Official, SelfMirror));
            Assert.AreEqual(item.ExpectedBmclapi, DreamPortMirrorPolicy.RewriteToMirror(item.Official, DreamPortMirrorPolicy.BmclapiHost));
        }

        const string thirdPartyUrl = "https://cdn.modrinth.com/data/abc/versions/1.0/mod.jar";
        Assert.AreEqual(thirdPartyUrl, DreamPortMirrorPolicy.RewriteToMirror(thirdPartyUrl, SelfMirror));
    }

    [TestMethod]
    public void BuildCandidateUrls_RespectsAllModesAndAvoidsDuplicateFabricMavenPath()
    {
        const string officialFabric = "https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.16.0/fabric-loader-0.16.0.jar";
        const string bmclapiFabric = "https://bmclapi2.bangbang93.com/maven/net/fabricmc/fabric-loader/0.16.0/fabric-loader-0.16.0.jar";
        const string expectedSelfFabric = "https://dl.xmcraft.cn/fabric-maven/net/fabricmc/fabric-loader/0.16.0/fabric-loader-0.16.0.jar";

        var auto = DreamPortMirrorPolicy.BuildCandidateUrls(
            [officialFabric], [bmclapiFabric], DreamPortDownloadSourceMode.Auto, SelfMirror);
        CollectionAssert.AreEqual(
            new[] { expectedSelfFabric, bmclapiFabric, officialFabric },
            auto.ToArray());

        var dpFirst = DreamPortMirrorPolicy.BuildCandidateUrls(
            [officialFabric], [bmclapiFabric], DreamPortDownloadSourceMode.DreamPort, SelfMirror);
        CollectionAssert.AreEqual(
            new[] { expectedSelfFabric, officialFabric, bmclapiFabric },
            dpFirst.ToArray());

        var bmclapiFirst = DreamPortMirrorPolicy.BuildCandidateUrls(
            [officialFabric], [bmclapiFabric], DreamPortDownloadSourceMode.Bmclapi, SelfMirror);
        CollectionAssert.AreEqual(
            new[] { bmclapiFabric, expectedSelfFabric, officialFabric },
            bmclapiFirst.ToArray());

        var officialFirst = DreamPortMirrorPolicy.BuildCandidateUrls(
            [officialFabric], [bmclapiFabric], DreamPortDownloadSourceMode.Official, SelfMirror);
        CollectionAssert.AreEqual(
            new[] { officialFabric, expectedSelfFabric, bmclapiFabric },
            officialFirst.ToArray());

        var disabledSelf = DreamPortMirrorPolicy.BuildCandidateUrls(
            [officialFabric], [bmclapiFabric], DreamPortDownloadSourceMode.Auto, string.Empty);
        CollectionAssert.AreEqual(
            new[] { bmclapiFabric, officialFabric },
            disabledSelf.ToArray());
    }

    [TestMethod]
    public void DownloadUserAgent_ContainsNoParenthesesOrUrl()
    {
        var dlUa = DreamPortBuildConfig.DownloadUserAgent;
        Assert.StartsWith("PCL-DreamPort/", dlUa);
        Assert.IsFalse(dlUa.Contains('('));
        Assert.IsFalse(dlUa.Contains(')'));
        Assert.IsFalse(dlUa.Contains("://", StringComparison.Ordinal));
    }
}
