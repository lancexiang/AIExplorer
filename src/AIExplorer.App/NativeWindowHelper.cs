using System.Runtime.InteropServices;

namespace AIExplorer_App;

internal static class NativeWindowHelper
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);

    private const int SwRestore = 9;

    public static void ActivateExistingMainWindow() => BringToForeground(FindWindow(null, "AIExplorer"));

    public static void BringToForeground(nint hwnd)
    {
        if (hwnd == 0)
        {
            return;
        }

        ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
    }

    public static bool TryGetCursorPos(out POINT point) => GetCursorPos(out point);
}
