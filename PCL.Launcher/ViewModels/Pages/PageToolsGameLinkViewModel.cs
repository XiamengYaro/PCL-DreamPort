using DreamPort.UI.ViewModels;
using PCL.Core.App;
using PCL.Core.Platform;
using PCL.Navigation;

namespace PCL;

/// <summary>
/// 梦港联机大厅视图模型（TCP/TLS 中继 + 局域网广播 + 房间/成员/好友邀请）。
/// </summary>
public sealed class PageToolsGameLinkViewModel : DreamPortLobbyViewModel
{
    public PageToolsGameLinkViewModel()
        : base(
            copyToClipboard: CopyTextAsync,
            getSelectedInstanceInfo: ResolveSelectedInstanceInfo,
            goToAccount: () => PageRouter.Navigate(PageIds.Tools, SubPageIds.ToolsDreamPortAccount),
            registerGlobalLifecycle: true)
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
