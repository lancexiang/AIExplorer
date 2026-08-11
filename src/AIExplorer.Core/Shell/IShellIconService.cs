namespace AIExplorer.Core.Shell;

/// <summary>
/// 系统图标服务。普通扩展名取类型图标；.lnk/.exe 等走真实路径解析。
/// </summary>
public interface IShellIconService
{
    /// <summary>返回图标缓存键；UI 层据此加载。</summary>
    string GetIconKey(string path, bool isDirectory);

    /// <summary>扩展名是否禁止 Shell 缩略图（如 .tif/.tiff）。</summary>
    bool IsThumbnailDisabled(string path);

    /// <summary>
    /// 获取小图标像素（自定义打包：width int32 + height int32 + BGRA）。
    /// </summary>
    Task<byte[]?> GetSmallIconPngAsync(string path, bool isDirectory, CancellationToken cancellationToken = default);
}
