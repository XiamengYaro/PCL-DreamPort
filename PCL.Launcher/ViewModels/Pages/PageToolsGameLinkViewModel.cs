using DreamPort.UI.ViewModels;
using PCL.Core.App;
using PCL.Core.Platform;
using PCL.Navigation;

namespace PCL;

/// <summary>
/// 梦港联机大厅视图模型（TCP/TLS 中继 + 局域网广播与日志兜底 + WebSocket 实时同步 + 房间/成员/好友邀请）。
/// </summary>
public sealed class PageToolsGameLinkViewModel : DreamPortLobbyViewModel
{
    public PageToolsGameLinkViewModel()
        : base(
            copyToClipboard: CopyTextAsync,
            getSelectedInstanceInfo: ResolveSelectedInstanceInfo,
            goToAccount: () => PageRouter.Navigate(PageIds.Tools, SubPageIds.ToolsDreamPortAccount),
            registerGlobalLifecycle: true,
            getCandidateInstanceDirs: ResolveCandidateInstanceDirs,
            getSelectedInstanceDir: ResolveSelectedInstanceDir,
            enableRealtime: true)
    {
    }

    private static async Task<bool> CopyTextAsync(string text)
    {
        try
        {
            if (ApplicationRuntime.GetService<IClipboardService>() is not { } clipboard)
                return false;

            await clipboard.SetTextAsync(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveSelectedInstanceDir()
    {
        try
        {
            var inst = InstanceList.McMcInstanceSelected;
            return inst?.PathIndie ?? inst?.PathInstance;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ResolveCandidateInstanceDirs()
    {
        var dirs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddInst(McInstance? inst)
        {
            if (inst is null)
                return;
            try
            {
                var p = inst.PathIndie;
                if (!string.IsNullOrWhiteSpace(p) && seen.Add(p))
                    dirs.Add(p);
            }
            catch
            {
                if (!string.IsNullOrWhiteSpace(inst.PathInstance) && seen.Add(inst.PathInstance))
                    dirs.Add(inst.PathInstance);
            }
        }

        AddInst(InstanceList.McMcInstanceSelected);
        try
        {
            foreach (var list in InstanceList.mcInstanceList.Values)
            {
                foreach (var item in list)
                    AddInst(item);
            }
        }
        catch
        {
            // 实例列表未加载完成时忽略
        }

        return dirs;
    }

    private static (string? Version, string? Loader) ResolveSelectedInstanceInfo()
    {
        var info = InstanceList.McMcInstanceSelected?.Info;
        if (info is null)
            return (null, null);

        var version = string.IsNullOrWhiteSpace(info.VanillaName)
            || string.Equals(info.VanillaName, McInstanceInfo.UnknownVanillaName, StringComparison.OrdinalIgnoreCase)
            ? null
            : info.VanillaName.Trim();

        var loader = info switch
        {
            { HasCleanroom: true } => "Cleanroom",
            { HasNeoForge: true } => "NeoForge",
            { HasForge: true } => "Forge",
            { HasFabric: true } => "Fabric",
            { HasQuilt: true } => "Quilt",
            { HasLegacyFabric: true } => "LegacyFabric",
            { HasLiteLoader: true } => "LiteLoader",
            { HasLabyMod: true } => "LabyMod",
            { HasOptiFine: true } => "OptiFine",
            _ => version is null ? null : "vanilla"
        };

        return (version, loader);
    }
}
