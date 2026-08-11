namespace AIExplorer.Core.Shell;

public sealed class ShellMenuItemDto
{
    public required string Text { get; init; }
    public string? Verb { get; init; }
    public int Id { get; init; }
    public bool IsSeparator { get; init; }
    public bool IsEnabled { get; init; } = true;
    public IReadOnlyList<ShellMenuItemDto> Children { get; init; } = [];
}

/// <summary>一次系统右键会话；调用方在菜单关闭后必须 Dispose。</summary>
public interface IShellContextMenuSession : IDisposable
{
    IReadOnlyList<ShellMenuItemDto> Items { get; }

    /// <param name="ownerHwnd">调用方窗口句柄；UI 线程上务必传入，否则部分 Shell 扩展会静默失败。</param>
    void Invoke(int commandId, nint ownerHwnd = 0);

    /// <param name="ownerHwnd">调用方窗口句柄；UI 线程上务必传入，否则部分 Shell 扩展会静默失败。</param>
    void InvokeVerb(string verb, nint ownerHwnd = 0);
}

/// <summary>
/// 系统 Shell 右键。WinUI 下不要用原生 TrackPopupMenu（易崩），
/// 应 GetItems → 填 WinUI MenuFlyout → Invoke。
/// </summary>
public interface IShellContextMenuService
{
    IShellContextMenuSession? Create(IReadOnlyList<string> paths);
}
