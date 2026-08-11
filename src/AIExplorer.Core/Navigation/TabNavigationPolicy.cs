namespace AIExplorer.Core.Navigation;

public enum TabNavigationAction
{
    NavigateInPlace,
    OpenNewTab,
}

/// <summary>锁定标签 = 固定路径：改变目录时新开 tab。</summary>
public static class TabNavigationPolicy
{
    public static TabNavigationAction Resolve(bool isLocked) =>
        isLocked ? TabNavigationAction.OpenNewTab : TabNavigationAction.NavigateInPlace;

    public static bool ShouldOpenNewTab(bool isLocked) =>
        Resolve(isLocked) == TabNavigationAction.OpenNewTab;
}
