using System.Runtime.InteropServices;
using System.Text;
using AIExplorer.Core.Shell;
using Vanara.PInvoke;
using Vanara.Windows.Shell;
using static Vanara.PInvoke.User32;

namespace AIExplorer.Infrastructure.Shell;

public sealed class ShellContextMenuService : IShellContextMenuService
{
    // QueryContextMenu 的命令 ID 起点；DTO 中保存的是相对偏移（wID - CmdFirst）。
    private const uint CmdFirst = 1;
    private const uint CmdLast = 0x7FFF;

    public IShellContextMenuSession? Create(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return null;
        }

        try
        {
            var items = new List<ShellItem>();
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    continue;
                }

                items.Add(new ShellItem(path));
            }

            if (items.Count == 0)
            {
                return null;
            }

            var menu = new ShellContextMenu([.. items]);

            // 不使用 Vanara 的 GetItems：它会对每一项调 GetCommandString 获取 verb，
            // 部分第三方 Shell 扩展在该调用里发生访问越界（AccessViolation），
            // .NET 8 无法捕获，进程直接崩溃。这里只走 QueryContextMenu + 菜单枚举，按 ID 调用。
            var hMenu = NativeMenu.CreatePopupMenu();
            menu.ComInterface.QueryContextMenu(hMenu, 0, CmdFirst, CmdLast, Shell32.CMF.CMF_NORMAL | Shell32.CMF.CMF_EXTENDEDVERBS);

            var entries = EnumerateMenu(hMenu);
            if (entries.Count == 0)
            {
                NativeMenu.DestroyMenu(hMenu);
                menu.Dispose();
                foreach (var item in items)
                {
                    item.Dispose();
                }

                return null;
            }

            return new Session(menu, items, entries, hMenu);
        }
        catch
        {
            return null;
        }
    }

    private static List<ShellMenuItemDto> EnumerateMenu(IntPtr hMenu)
    {
        var list = new List<ShellMenuItemDto>();
        var count = NativeMenu.GetMenuItemCount(hMenu);
        for (var i = 0; i < count; i++)
        {
            var state = NativeMenu.GetMenuState(hMenu, (uint)i, NativeMenu.MF_BYPOSITION);
            var isSep = (state & NativeMenu.MF_SEPARATOR) != 0;

            var sb = new StringBuilder(512);
            NativeMenu.GetMenuString(hMenu, (uint)i, sb, sb.Capacity, NativeMenu.MF_BYPOSITION);
            var rawText = sb.ToString();
            // 去掉助记符 & 和 \t 之后的加速键文本
            var tabIndex = rawText.IndexOf('\t');
            if (tabIndex >= 0)
            {
                rawText = rawText[..tabIndex];
            }

            var text = rawText.Replace("&", string.Empty).Trim();
            if (!isSep && string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var hSub = NativeMenu.GetSubMenu(hMenu, i);
            IReadOnlyList<ShellMenuItemDto> children = [];
            if (hSub != IntPtr.Zero)
            {
                children = EnumerateMenu(hSub);
                if (children.Count == 0)
                {
                    // 懒加载子菜单（如“发送到”）此时为空，跳过以免出现空的二级项
                    continue;
                }
            }

            var rawId = NativeMenu.GetMenuItemID(hMenu, i);
            var offset = rawId is not uint.MaxValue and >= CmdFirst ? (int)(rawId - CmdFirst) : -1;
            var enabled = (state & (NativeMenu.MF_DISABLED | NativeMenu.MF_GRAYED)) == 0;

            list.Add(new ShellMenuItemDto
            {
                Text = isSep ? string.Empty : text,
                Verb = null,
                Id = offset,
                IsSeparator = isSep,
                IsEnabled = enabled,
                Children = children,
            });
        }

        return list;
    }

    private static class NativeMenu
    {
        public const uint MF_BYPOSITION = 0x400;
        public const uint MF_SEPARATOR = 0x800;
        public const uint MF_DISABLED = 0x2;
        public const uint MF_GRAYED = 0x1;

        [DllImport("user32.dll")]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll")]
        public static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        public static extern int GetMenuItemCount(IntPtr hMenu);

        [DllImport("user32.dll")]
        public static extern uint GetMenuItemID(IntPtr hMenu, int nPos);

        [DllImport("user32.dll")]
        public static extern IntPtr GetSubMenu(IntPtr hMenu, int nPos);

        [DllImport("user32.dll")]
        public static extern uint GetMenuState(IntPtr hMenu, uint uId, uint uFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetMenuString(IntPtr hMenu, uint uIDItem, StringBuilder lpString, int cchMax, uint flags);
    }

    private sealed class Session : IShellContextMenuSession
    {
        private readonly ShellContextMenu _menu;
        private readonly List<ShellItem> _items;
        private readonly IntPtr _hMenu;
        private bool _disposed;

        public Session(ShellContextMenu menu, List<ShellItem> items, IReadOnlyList<ShellMenuItemDto> entries, IntPtr hMenu)
        {
            _menu = menu;
            _items = items;
            _hMenu = hMenu;
            Items = entries;
        }

        public IReadOnlyList<ShellMenuItemDto> Items { get; }

        public void Invoke(int commandId, nint ownerHwnd = 0)
        {
            if (_disposed || commandId < 0)
            {
                return;
            }

            try
            {
                // 对齐 Files：CMINVOKECOMMANDINFOEX(offset) + hwnd + lpDirectoryW
                // 旧写法 hwnd=default 时，压缩等扩展常会静默失败
                var hwnd = ownerHwnd != 0 ? new HWND(ownerHwnd) : User32.GetActiveWindow();
                var workDir = TryGetWorkingDirectory();
                var pici = new Shell32.CMINVOKECOMMANDINFOEX(commandId)
                {
                    nShow = ShowWindowCommand.SW_SHOWNORMAL,
                    hwnd = hwnd,
                    fMask = Shell32.CMIC.CMIC_MASK_UNICODE,
                };
                if (!string.IsNullOrWhiteSpace(workDir))
                {
                    pici.lpDirectoryW = workDir;
                }

                pici.cbSize = (uint)Marshal.SizeOf(pici);
                var hr = _menu.ComInterface.InvokeCommand(pici);
                if (hr.Failed)
                {
                    ResourceId id = commandId;
                    _menu.InvokeCommand(id, ShowWindowCommand.SW_SHOWNORMAL, hwnd);
                }
            }
            catch
            {
                try
                {
                    ResourceId id = commandId;
                    var hwnd = ownerHwnd != 0 ? new HWND(ownerHwnd) : User32.GetActiveWindow();
                    _menu.InvokeCommand(id, ShowWindowCommand.SW_SHOWNORMAL, hwnd);
                }
                catch
                {
                }
            }
        }

        public void InvokeVerb(string verb, nint ownerHwnd = 0)
        {
            if (_disposed || string.IsNullOrWhiteSpace(verb))
            {
                return;
            }

            try
            {
                var hwnd = ownerHwnd != 0 ? new HWND(ownerHwnd) : User32.GetActiveWindow();
                _menu.InvokeVerb(verb, ShowWindowCommand.SW_SHOWNORMAL, hwnd);
            }
            catch
            {
            }
        }

        private string? TryGetWorkingDirectory()
        {
            try
            {
                foreach (var item in _items)
                {
                    var path = item.FileSystemPath;
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    if (Directory.Exists(path))
                    {
                        return path;
                    }

                    var parent = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(parent))
                    {
                        return parent;
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                NativeMenu.DestroyMenu(_hMenu);
            }
            catch
            {
            }

            try
            {
                _menu.Dispose();
            }
            catch
            {
            }

            foreach (var item in _items)
            {
                try
                {
                    item.Dispose();
                }
                catch
                {
                }
            }
        }
    }
}
