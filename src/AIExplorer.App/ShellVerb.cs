using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace AIExplorer_App;

/// <summary>
/// 打开资源管理器同款「属性」对话框（单选 / 多选合并属性页）。
/// </summary>
internal static class ShellVerb
{
    private const uint SeeMaskInvokeIdList = 0x0000000C;
    private const uint ShopFilePath = 0x00000002;
    private const int SwShow = 5;

    public static bool ShowProperties(string path, IntPtr ownerHwnd = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return false;
        }

        ownerHwnd = ResolveOwner(ownerHwnd);

        var info = new ShellExecuteInfo
        {
            cbSize = Marshal.SizeOf<ShellExecuteInfo>(),
            fMask = SeeMaskInvokeIdList,
            hwnd = ownerHwnd,
            lpVerb = "properties",
            lpFile = path,
            nShow = SwShow,
        };

        if (ShellExecuteEx(ref info))
        {
            return true;
        }

        return SHObjectProperties(ownerHwnd, ShopFilePath, path, null);
    }

    /// <summary>单选走 ShellExecuteEx；多选走 SHMultiFileProperties（系统合并属性页）。</summary>
    public static bool ShowProperties(IReadOnlyList<string> paths, IntPtr ownerHwnd = default)
    {
        var existing = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(p))
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (existing.Count == 0)
        {
            return false;
        }

        if (existing.Count == 1)
        {
            return ShowProperties(existing[0], ownerHwnd);
        }

        try
        {
            var data = new ShellIdListDataObject(existing);
            var hr = SHMultiFileProperties(data, 0);
            return hr == 0;
        }
        catch
        {
            // 多选失败时至少打开第一项，避免完全无反馈
            return ShowProperties(existing[0], ownerHwnd);
        }
    }

    private static IntPtr ResolveOwner(IntPtr ownerHwnd)
    {
        if (ownerHwnd != IntPtr.Zero)
        {
            return ownerHwnd;
        }

        try
        {
            return App.WindowHandle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo lpExecInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHObjectProperties(
        IntPtr hwnd,
        uint shopObjectType,
        string pszObjectName,
        string? pszPropertyPage);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHMultiFileProperties(
        System.Runtime.InteropServices.ComTypes.IDataObject pdtobj,
        uint dwFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILCreateFromPathW(string pszPath);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern uint ILGetSize(IntPtr pidl);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM pmedium);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    /// <summary>仅承载 Shell IDList Array + CF_HDROP，供 SHMultiFileProperties 使用。</summary>
    private sealed class ShellIdListDataObject : System.Runtime.InteropServices.ComTypes.IDataObject
    {
        private const uint GmemMoveable = 0x0002;
        private static readonly short CfShellIdList = (short)RegisterClipboardFormat("Shell IDList Array");
        private static readonly short CfHdrop = 15; // CF_HDROP

        private readonly byte[] _shellIdList;
        private readonly byte[] _hdrop;

        public ShellIdListDataObject(IReadOnlyList<string> paths)
        {
            _shellIdList = CreateShellIdListBytes(paths);
            _hdrop = CreateHdropBytes(paths);
        }

        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            medium = default;
            var data = ResolveFormat(format.cfFormat);
            if (data is null || (format.tymed & TYMED.TYMED_HGLOBAL) == 0)
            {
                throw new COMException("Invalid format", unchecked((int)0x80040064)); // DV_E_FORMATETC
            }

            var hglobal = GlobalAlloc(GmemMoveable, (UIntPtr)data.Length);
            if (hglobal == IntPtr.Zero)
            {
                throw new OutOfMemoryException();
            }

            var ptr = GlobalLock(hglobal);
            try
            {
                Marshal.Copy(data, 0, ptr, data.Length);
            }
            finally
            {
                GlobalUnlock(hglobal);
            }

            medium.tymed = TYMED.TYMED_HGLOBAL;
            medium.unionmember = hglobal;
            medium.pUnkForRelease = null;
        }

        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) =>
            throw new NotImplementedException();

        public int QueryGetData(ref FORMATETC format)
        {
            if ((format.tymed & TYMED.TYMED_HGLOBAL) == 0 || format.dwAspect != DVASPECT.DVASPECT_CONTENT)
            {
                return unchecked((int)0x80040064);
            }

            return ResolveFormat(format.cfFormat) is null ? unchecked((int)0x80040064) : 0;
        }

        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
        {
            formatOut = formatIn;
            return 0x000401F0; // DATA_S_SAMEFORMATETC
        }

        public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
        {
            if (release)
            {
                ReleaseStgMedium(ref medium);
            }

            throw new NotImplementedException();
        }

        public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
        {
            if (direction != DATADIR.DATADIR_GET)
            {
                throw new NotImplementedException();
            }

            return new FormatEnumerator(
            [
                new FORMATETC
                {
                    cfFormat = CfShellIdList,
                    dwAspect = DVASPECT.DVASPECT_CONTENT,
                    lindex = -1,
                    tymed = TYMED.TYMED_HGLOBAL,
                },
                new FORMATETC
                {
                    cfFormat = CfHdrop,
                    dwAspect = DVASPECT.DVASPECT_CONTENT,
                    lindex = -1,
                    tymed = TYMED.TYMED_HGLOBAL,
                },
            ]);
        }

        public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
        {
            connection = 0;
            return unchecked((int)0x80040003); // OLE_E_ADVISENOTSUPPORTED
        }

        public void DUnadvise(int connection)
        {
        }

        public int EnumDAdvise(out IEnumSTATDATA? enumAdvise)
        {
            enumAdvise = null;
            return unchecked((int)0x80040003);
        }

        private byte[]? ResolveFormat(short cfFormat)
        {
            if (cfFormat == CfShellIdList)
            {
                return _shellIdList;
            }

            if (cfFormat == CfHdrop)
            {
                return _hdrop;
            }

            return null;
        }

        private static byte[] CreateShellIdListBytes(IReadOnlyList<string> paths)
        {
            var pidls = new List<byte[]>(paths.Count);
            foreach (var path in paths)
            {
                var pidl = ILCreateFromPathW(path);
                if (pidl == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    var size = (int)ILGetSize(pidl);
                    var bytes = new byte[size];
                    Marshal.Copy(pidl, bytes, 0, size);
                    pidls.Add(bytes);
                }
                finally
                {
                    ILFree(pidl);
                }
            }

            if (pidls.Count == 0)
            {
                throw new InvalidOperationException("无法为选中项创建 PIDL。");
            }

            // CIDA: cidl + aoffset[cidl+1] + pidlParent(empty) + child pidls
            var offset = 4 * (pidls.Count + 2);
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(pidls.Count);
            bw.Write(offset); // parent offset
            offset += 4; // empty parent written as Int32 0
            foreach (var pidl in pidls)
            {
                bw.Write(offset);
                offset += pidl.Length;
            }

            bw.Write(0); // parent pidl (empty)
            foreach (var pidl in pidls)
            {
                bw.Write(pidl);
            }

            return ms.ToArray();
        }

        private static byte[] CreateHdropBytes(IReadOnlyList<string> paths)
        {
            // DROPFILES + double-null-terminated Unicode paths
            var pathBlob = new StringBuilder();
            foreach (var path in paths)
            {
                pathBlob.Append(path);
                pathBlob.Append('\0');
            }

            pathBlob.Append('\0');
            var pathBytes = Encoding.Unicode.GetBytes(pathBlob.ToString());
            const int dropFilesSize = 20; // DROPFILES struct size on x64 with fWide
            var result = new byte[dropFilesSize + pathBytes.Length];
            BitConverter.GetBytes(dropFilesSize).CopyTo(result, 0); // pFiles
            // pt.x, pt.y = 0
            // fNC = 0
            BitConverter.GetBytes(1).CopyTo(result, 16); // fWide = TRUE
            Buffer.BlockCopy(pathBytes, 0, result, dropFilesSize, pathBytes.Length);
            return result;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterClipboardFormat(string format);

        private sealed class FormatEnumerator : IEnumFORMATETC
        {
            private readonly FORMATETC[] _formats;
            private int _index;

            public FormatEnumerator(FORMATETC[] formats) => _formats = formats;

            public void Clone(out IEnumFORMATETC newEnum) =>
                newEnum = new FormatEnumerator(_formats) { _index = _index };

            public int Next(int celt, FORMATETC[] rgelt, int[]? pceltFetched)
            {
                var fetched = 0;
                while (fetched < celt && _index < _formats.Length)
                {
                    rgelt[fetched++] = _formats[_index++];
                }

                if (pceltFetched is { Length: > 0 })
                {
                    pceltFetched[0] = fetched;
                }

                return fetched == celt ? 0 : 1;
            }

            public int Reset()
            {
                _index = 0;
                return 0;
            }

            public int Skip(int celt)
            {
                _index = Math.Min(_formats.Length, _index + celt);
                return _index >= _formats.Length ? 1 : 0;
            }
        }
    }
}
