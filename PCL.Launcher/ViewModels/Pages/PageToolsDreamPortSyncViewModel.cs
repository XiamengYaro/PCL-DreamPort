using DreamPort.UI.ViewModels;

namespace PCL;

/// <summary>
/// 梦港「同步与工具」页面视图模型（服务器配套模组双源检测/兼容硬拦/增量同步；后续扩展云存档/配置同步/AI 排障）。
/// </summary>
public sealed class PageToolsDreamPortSyncViewModel : DreamPortSyncViewModel
{
    public PageToolsDreamPortSyncViewModel()
        : base(getSelectedInstance: ResolveSelectedInstance)
    {
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
