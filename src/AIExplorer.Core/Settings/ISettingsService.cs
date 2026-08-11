namespace AIExplorer.Core.Settings;

public sealed class FeatureFlags
{
    /// <summary>性能模式：一键关闭 Probe/缩略图/预览类重操作。</summary>
    public bool PerformanceMode { get; set; } = true;

    public bool EnableImageProbeColumns { get; set; }
    public bool EnableListThumbnails { get; set; }
    public bool EnableFolderRecursiveSize { get; set; }

    /// <summary>复制映射网络盘路径时转换为 \\server\share 形式；默认启用。</summary>
    public bool CopyMappedDriveAsUnc { get; set; } = true;

    /// <summary>文件列表列显隐（名称列固定显示）。</summary>
    public bool ShowSizeColumn { get; set; } = true;
    public bool ShowTypeColumn { get; set; } = true;
    public bool ShowModifiedColumn { get; set; } = true;

    /// <summary>详情列宽（像素）；名称列仍吃剩余空间。</summary>
    public double SizeColumnWidth { get; set; } = 90;
    public double TypeColumnWidth { get; set; } = 70;
    public double ModifiedColumnWidth { get; set; } = 200;

    /// <summary>详情排序：Name / Size / Type / Modified</summary>
    public string SortColumn { get; set; } = "Name";
    public bool SortAscending { get; set; } = true;

    /// <summary>
    /// 侧栏目录树是否跟随当前路径展开并定位；关闭则不自动深展开。
    /// 「收起」按钮为一次性动作，与本开关无关。默认开启。
    /// </summary>
    public bool AutoRevealInTree { get; set; } = true;

    /// <summary>侧栏目录树抽屉是否展开。默认展开，并在切换时持久化。</summary>
    public bool NavDrawerExpanded { get; set; } = true;

    /// <summary>Default / Light / Dark</summary>
    public string Theme { get; set; } = "Default";

    /// <summary>强调色：Default（系统）/ Ocean / Forest / Sunset / Violet</summary>
    public string AccentColor { get; set; } = "Default";

    /// <summary>窗口背景：Mica / Acrylic / None（纯色）</summary>
    public string WindowBackdrop { get; set; } = "Mica";
}

/// <summary>窗口背景选项（与设置页一致）。</summary>
public static class WindowBackdropOptions
{
    public const string Mica = "Mica";
    public const string Acrylic = "Acrylic";
    public const string None = "None";

    public static IReadOnlyList<string> All { get; } = [Mica, Acrylic, None];

    public static bool IsKnown(string? name) =>
        name is Mica or Acrylic or None;
}

/// <summary>应用强调色色板（与设置页选项一致）。</summary>
public static class AccentPalette
{
    public static IReadOnlyList<string> Options { get; } =
        ["Default", "Ocean", "Forest", "Sunset", "Violet"];

    /// <summary>返回 RGB；Default 表示跟随系统（不覆盖）。</summary>
    public static (byte R, byte G, byte B)? ResolveRgb(string? name) => name switch
    {
        "Ocean" => (0, 120, 212),
        "Forest" => (16, 137, 62),
        "Sunset" => (196, 89, 17),
        "Violet" => (136, 62, 193),
        _ => null,
    };
}

public interface ISettingsService
{
    FeatureFlags Features { get; }

    /// <summary>文件颜色标记调色板（含默认五色，可在设置中改显示名/色值/含义）。</summary>
    IList<FileColorDefinition> FileColors { get; }

    bool IsExtensionEnabled(string extensionId);
    void SetExtensionEnabled(string extensionId, bool enabled);
    Task LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CancellationToken cancellationToken = default);
    string? GetExtensionSetting(string extensionId, string key);
    void SetExtensionSetting(string extensionId, string key, string? value);
}
