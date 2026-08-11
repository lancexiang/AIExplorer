using System.Runtime.InteropServices;
using AIExplorer.Core.Shell;

namespace AIExplorer.Infrastructure.Shell;

/// <summary>
/// 系统图标：普通扩展名按类型缓存；.lnk/.exe 等走真实路径解析（快捷方式目标图标）。
/// .tif/.tiff 禁止缩略图路径，仅返回类型图标。
/// </summary>
public sealed class ShellIconService : IShellIconService
{
    private static readonly HashSet<string> ThumbnailBlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tif", ".tiff", ".ome.tif", ".ome.tiff",
    };

    private static readonly HashSet<string> RealPathExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lnk", ".exe", ".ico", ".url", ".msc", ".cpl",
    };

    private readonly Dictionary<string, byte[]?> _pngCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();

    public string GetIconKey(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return "folder";
        }

        if (NeedsRealPathIcon(path))
        {
            try
            {
                return "path:" + Path.GetFullPath(path);
            }
            catch
            {
                return "path:" + path;
            }
        }

        var ext = Path.GetExtension(path);
        return string.IsNullOrEmpty(ext) ? "file" : ext.ToLowerInvariant();
    }

    public bool IsThumbnailDisabled(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var blocked in ThumbnailBlockedExtensions)
        {
            if (name.EndsWith(blocked, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>获取小图标 BGRA（缓存）。</summary>
    public Task<byte[]?> GetSmallIconPngAsync(string path, bool isDirectory, CancellationToken cancellationToken = default)
    {
        var key = GetIconKey(path, isDirectory);
        lock (_cacheLock)
        {
            if (_pngCache.TryGetValue(key, out var cached))
            {
                return Task.FromResult(cached);
            }
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var png = ExtractIconPng(path, isDirectory);
            // 真实路径图标失败时不永久缓存 null，避免一次失败锁死该项
            if (png is not null || !NeedsRealPathIcon(path))
            {
                lock (_cacheLock)
                {
                    _pngCache[key] = png;
                }
            }

            return png;
        }, cancellationToken);
    }

    private static bool NeedsRealPathIcon(string path)
    {
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && RealPathExtensions.Contains(ext);
    }

    private static byte[]? ExtractIconPng(string path, bool isDirectory)
    {
        if (!isDirectory && NeedsRealPathIcon(path) && !string.IsNullOrWhiteSpace(path))
        {
            // 真实路径：让 Shell 解析 .lnk 目标 / EXE 自身图标
            var real = SHGetFileInfo(
                path,
                0,
                out var realInfo,
                (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_ICON | SHGFI_SMALLICON);
            if (real != IntPtr.Zero && realInfo.hIcon != IntPtr.Zero)
            {
                try
                {
                    return IconToBgra(realInfo.hIcon);
                }
                finally
                {
                    DestroyIcon(realInfo.hIcon);
                }
            }
        }

        // 回退：按扩展名/目录属性取类型图标（不打开文件内容）
        var flags = SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES;
        var attrs = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        var displayPath = isDirectory
            ? (string.IsNullOrWhiteSpace(path) ? @"C:\Windows" : path)
            : (string.IsNullOrEmpty(Path.GetExtension(path)) ? "file" : "file" + Path.GetExtension(path));

        var result = SHGetFileInfo(displayPath, attrs, out var info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return IconToBgra(info.hIcon);
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private static byte[]? IconToBgra(IntPtr hIcon)
    {
        const int size = 16;
        var hdcScreen = GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero)
        {
            return null;
        }

        var hdcMem = CreateCompatibleDC(hdcScreen);
        if (hdcMem == IntPtr.Zero)
        {
            ReleaseDC(IntPtr.Zero, hdcScreen);
            return null;
        }

        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = -size,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            },
        };

        var hBitmap = CreateDIBSection(hdcMem, ref bmi, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        if (hBitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcScreen);
            return null;
        }

        var old = SelectObject(hdcMem, hBitmap);
        try
        {
            var pixelCount = size * size * 4;
            for (var i = 0; i < pixelCount; i++)
            {
                Marshal.WriteByte(bits, i, 0);
            }

            DrawIconEx(hdcMem, 0, 0, hIcon, size, size, 0, IntPtr.Zero, DI_NORMAL);

            var pixels = new byte[pixelCount];
            Marshal.Copy(bits, pixels, 0, pixelCount);

            var anyAlpha = false;
            for (var i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 0)
                {
                    anyAlpha = true;
                    break;
                }
            }

            if (!anyAlpha)
            {
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0)
                    {
                        pixels[i + 3] = 255;
                    }
                }
            }

            for (var i = 0; i < pixels.Length; i += 4)
            {
                var a = pixels[i + 3];
                if (a == 0)
                {
                    pixels[i] = 0;
                    pixels[i + 1] = 0;
                    pixels[i + 2] = 0;
                }
                else if (a < 255)
                {
                    pixels[i] = (byte)(pixels[i] * a / 255);
                    pixels[i + 1] = (byte)(pixels[i + 1] * a / 255);
                    pixels[i + 2] = (byte)(pixels[i + 2] * a / 255);
                }
            }

            using var ms = new MemoryStream(8 + pixels.Length);
            ms.Write(BitConverter.GetBytes(size));
            ms.Write(BitConverter.GetBytes(size));
            ms.Write(pixels);
            return ms.ToArray();
        }
        finally
        {
            SelectObject(hdcMem, old);
            DeleteObject(hBitmap);
            DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const uint DI_NORMAL = 0x0003;
    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, out SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyHeight, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
