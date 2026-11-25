using DreamPort.UI.ViewModels;
using PCL.Core.Minecraft.Profile;

namespace PCL;

/// <summary>
/// 梦港「同步与工具」页面视图模型：绑定当前选中的 Minecraft 实例、PCL 全局白名单配置与正版玩家身份脱敏列表。
/// </summary>
public class PageToolsDreamPortSyncViewModel : DreamPortSyncViewModel
{
    public PageToolsDreamPortSyncViewModel()
        : base(
            getSelectedInstance: ResolveSelectedInstance,
            getExtraIdentities: ResolveExtraIdentities)
    {
    }

    private static IReadOnlyList<string> ResolveExtraIdentities()
    {
        var list = new List<string>();
        try
        {
            if (ProfileService.Current is { } current)
            {
                if (!string.IsNullOrWhiteSpace(current.UserName))
                    list.Add(current.UserName);
                if (!string.IsNullOrWhiteSpace(current.Uuid))
                    list.Add(current.Uuid);
            }

            foreach (var p in ProfileService.Profiles)
            {
                if (!string.IsNullOrWhiteSpace(p.UserName))
                    list.Add(p.UserName);
                if (!string.IsNullOrWhiteSpace(p.Uuid))
                    list.Add(p.Uuid);
            }
        }
        catch
        {
            // ignore
        }
        return list;
    }

    private static (string? InstanceDir, string? InstanceName, string? GameVersion, string? Loader) ResolveSelectedInstance()
    {
        var inst = InstanceList.McMcInstanceSelected;
        if (inst is null)
            return (null, null, null, null);

        var info = inst.Info;
        var ver = info is not null
            && !string.IsNullOrWhiteSpace(info.VanillaName)
            && !string.Equals(info.VanillaName, McInstanceInfo.UnknownVanillaName, StringComparison.OrdinalIgnoreCase)
            ? info.VanillaName.Trim()
            : null;

        var loader = info switch
        {
            null => null,
            { HasCleanroom: true } => "cleanroom",
            { HasNeoForge: true } => "neoforge",
            { HasForge: true } => "forge",
            { HasFabric: true } => "fabric",
            { HasQuilt: true } => "quilt",
            { HasLegacyFabric: true } => "legacyfabric",
            { HasLiteLoader: true } => "liteloader",
            { HasLabyMod: true } => "labymod",
            { HasOptiFine: true } => "optifine",
            _ => ver is null ? null : "vanilla"
        };

        string? dir;
        try
        {
            dir = inst.PathIndie;
        }
        catch
        {
            dir = inst.PathInstance;
        }

        return (dir, inst.Name, ver, loader);
    }
}
