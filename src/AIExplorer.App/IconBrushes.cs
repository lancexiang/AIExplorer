using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AIExplorer_App;

/// <summary>
/// 图标语义配色（对齐 Win11 / Allen 风格）：文件夹金黄、驱动器灰蓝、快速访问蓝、网络青。
/// SolidColorBrush 需在 UI 线程首次创建；这些属性只会被 XAML 绑定在 UI 线程访问。
/// </summary>
public static class IconBrushes
{
    public static SolidColorBrush Folder { get; } = new(Color.FromArgb(255, 0xF8, 0xC5, 0x4A));
    public static SolidColorBrush Drive { get; } = new(Color.FromArgb(255, 0x8C, 0xA3, 0xB8));
    public static SolidColorBrush DriveSsd { get; } = new(Color.FromArgb(255, 0x3D, 0xB8, 0x8C));
    public static SolidColorBrush DriveHdd { get; } = new(Color.FromArgb(255, 0x8C, 0xA3, 0xB8));
    public static SolidColorBrush Accent { get; } = new(Color.FromArgb(255, 0x3D, 0xB8, 0x8C));
    public static SolidColorBrush QuickAccess { get; } = new(Color.FromArgb(255, 0x4F, 0x9C, 0xE8));
    public static SolidColorBrush Network { get; } = new(Color.FromArgb(255, 0x3F, 0xB6, 0xA8));
    public static SolidColorBrush File { get; } = new(Color.FromArgb(255, 0x9A, 0xA2, 0xAB));
    public static SolidColorBrush Image { get; } = new(Color.FromArgb(255, 0x6C, 0xB5, 0x6C));
    public static SolidColorBrush Media { get; } = new(Color.FromArgb(255, 0xB5, 0x7E, 0xD8));
    public static SolidColorBrush Code { get; } = new(Color.FromArgb(255, 0xE8, 0x8B, 0x5A));
    public static SolidColorBrush Favorite { get; } = new(Color.FromArgb(255, 0xF2, 0xB0, 0x2E));

    // 收藏分组：紫罗兰，和文件列表的金黄文件夹形成明显区分
    public static SolidColorBrush FavoriteGroup { get; } = new(Color.FromArgb(255, 0x8B, 0x7C, 0xF0));

    // 特殊文件夹（对齐 Allen/Win11 主页配色）
    public static SolidColorBrush Desktop { get; } = new(Color.FromArgb(255, 0x3A, 0x8D, 0xDE));
    public static SolidColorBrush Documents { get; } = new(Color.FromArgb(255, 0x4C, 0x8B, 0xC4));
    public static SolidColorBrush Downloads { get; } = new(Color.FromArgb(255, 0x2E, 0xA8, 0x9A));
    public static SolidColorBrush Music { get; } = new(Color.FromArgb(255, 0xE0, 0x6A, 0x7A));
    public static SolidColorBrush Home { get; } = new(Color.FromArgb(255, 0x5A, 0x9B, 0xD8));

    // 文档/压缩/可执行等分型色
    public static SolidColorBrush Pdf { get; } = new(Color.FromArgb(255, 0xD9, 0x4A, 0x3D));
    public static SolidColorBrush Doc { get; } = new(Color.FromArgb(255, 0x2B, 0x7C, 0xD3));
    public static SolidColorBrush Sheet { get; } = new(Color.FromArgb(255, 0x2E, 0x9E, 0x5B));
    public static SolidColorBrush Slide { get; } = new(Color.FromArgb(255, 0xE0, 0x7A, 0x2E));
    public static SolidColorBrush Archive { get; } = new(Color.FromArgb(255, 0xC9, 0x9A, 0x3E));
    public static SolidColorBrush Executable { get; } = new(Color.FromArgb(255, 0x5B, 0x8A, 0xA6));

    /// <summary>按特殊文件夹 glyph 返回语义色；未知返回 QuickAccess。</summary>
    public static SolidColorBrush ForGlyph(string glyph) => glyph switch
    {
        "\uE8FC" => Desktop,     // 桌面
        "\uE8A5" => Documents,   // 文档
        "\uE896" => Downloads,   // 下载
        "\uEB9F" => Image,       // 图片
        "\uEC4F" => Music,       // 音乐
        "\uE714" => Media,       // 视频
        "\uE77B" => Home,        // 主文件夹
        _ => QuickAccess,
    };
}

/// <summary>文件寿命徽章色（对齐 Allen Explorer 修改时间分档）。</summary>
public static class AgeBrushes
{
    public static SolidColorBrush Hot { get; } = new(Color.FromArgb(255, 0xE0, 0x5A, 0x4A));       // 今天/小时内：红
    public static SolidColorBrush Warm { get; } = new(Color.FromArgb(255, 0xE0, 0x9A, 0x3E));      // 昨天：橙
    public static SolidColorBrush Week { get; } = new(Color.FromArgb(255, 0x3C, 0xB3, 0x71));      // 本周：绿
    public static SolidColorBrush Month { get; } = new(Color.FromArgb(255, 0x4F, 0x9C, 0xE8));     // 本月：蓝
    public static SolidColorBrush Year { get; } = new(Color.FromArgb(255, 0x7A, 0x8A, 0x99));      // 今年：灰蓝
    public static SolidColorBrush LastYear { get; } = new(Color.FromArgb(255, 0x9A, 0xA2, 0xAB));  // 去年
    public static SolidColorBrush Older { get; } = new(Color.FromArgb(255, 0xB0, 0xB6, 0xBC));     // 更早
}
