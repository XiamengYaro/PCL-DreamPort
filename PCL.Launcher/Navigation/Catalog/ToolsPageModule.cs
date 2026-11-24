using Microsoft.Extensions.DependencyInjection;
using PCL.Core.App;
using PCL.Views.Pages.Tools;
using static PCL.Navigation.PageDescriptorFactory;

namespace PCL.Navigation.Catalog;

public sealed class ToolsPageModule : IPageModule
{
    private static SubPageDescriptor[] ToolsSubPages { get; } =
    [
        Sub(
            PageIds.Tools,
            SubPageIds.ToolsGameLink,
            "Tools.Left.Lobby",
            "lucide/link-2",
            "multiplayer",
            0,
            (sp, _) => ActivatorUtilities.CreateInstance<PageToolsGameLink>(sp),
            refresh: true,
            visible: () => !Config.Preference.Hide.ToolsGameLink),
        Sub(
            PageIds.Tools,
            SubPageIds.ToolsTest,
            "Tools.Left.Utilities.Title",
            "lucide/tool-case",
            "utilities",
            1,
            (sp, _) => ActivatorUtilities.CreateInstance<PageToolsTest>(sp),
            () => !Config.Preference.Hide.ToolsTest),
        Sub(
            PageIds.Tools,
            SubPageIds.ToolsDreamPortAccount,
            "Tools.Left.DreamPortAccount",
            "lucide/user",
            "dreamport",
            2,
            (sp, _) => ActivatorUtilities.CreateInstance<PageToolsDreamPortAccount>(sp))
    ];

    public IReadOnlyList<PageDescriptor> Pages { get; } =
    [
        new(
            PageIds.Tools,
            "Tools.Left.Lobby",
            "lucide/wrench",
            MainTab.Tools,
            150d,
            SubPageIds.ToolsGameLink,
            [
                new PageGroupDescriptor("multiplayer", "Tools.Left.Multiplayer", 0),
                new PageGroupDescriptor("utilities", "Tools.Left.Utilities", 1),
                new PageGroupDescriptor("dreamport", "Tools.Left.DreamPort", 2)
            ],
            ToolsSubPages,
            (sp, descriptor) => ActivatorUtilities.CreateInstance<ToolsPageHost>(sp, descriptor))
    ];

    public IReadOnlyList<MainTabDescriptor> MainTabs { get; } =
    [
        new(
            MainTab.Tools,
            PageIds.Tools,
            "Main.Tab.Tools",
            "lucide/wrench",
            3)
    ];
}