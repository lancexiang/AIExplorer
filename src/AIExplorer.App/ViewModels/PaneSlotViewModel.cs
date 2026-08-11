using CommunityToolkit.Mvvm.ComponentModel;

namespace AIExplorer_App.ViewModels;

/// <summary>工作区一栏：Primary 为左栏（BrowserTabs），其余为标准文件 Tab 组。</summary>
public sealed class PaneSlotViewModel
{
    public PaneSlotViewModel(PaneGroupViewModel group)
    {
        IsPrimary = false;
        Group = group;
    }

    private PaneSlotViewModel()
    {
        IsPrimary = true;
        Group = null;
    }

    public static PaneSlotViewModel CreatePrimary() => new();

    public bool IsPrimary { get; }

    /// <summary>非 Primary 栏的文件标签组；Primary 为 null。</summary>
    public PaneGroupViewModel? Group { get; }

    /// <summary>相对宽度/高度比例，默认 1。</summary>
    public double Ratio { get; set; } = 1.0;
}
