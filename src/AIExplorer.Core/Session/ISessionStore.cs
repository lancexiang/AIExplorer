namespace AIExplorer.Core.Session;

public sealed class SessionState
{
    public int SelectedIndex { get; set; }

    /// <summary>全局分栏开关（与具体左 Tab 解耦）。</summary>
    public bool IsDualPane { get; set; }

    /// <summary>Horizontal / Vertical</summary>
    public string Orientation { get; set; } = "Horizontal";

    /// <summary>Left / Right — 侧栏导航跟激活栏。</summary>
    public string ActivePaneSide { get; set; } = "Left";

    /// <summary>右侧独立标签组路径（按 ActiveIndex 顺序）。</summary>
    public List<string> RightPanePaths { get; set; } = [];

    public int RightSelectedIndex { get; set; }

    public List<SessionTabState> Tabs { get; set; } = [];
}

public sealed class SessionTabState
{
    public string LeftPath { get; set; } = string.Empty;
    /// <summary>兼容旧会话；新会话右侧路径存在 SessionState.RightPanePaths。</summary>
    public string? RightPath { get; set; }
    /// <summary>兼容旧会话；以 SessionState.IsDualPane 为准。</summary>
    public bool IsDualPane { get; set; }
    public bool IsLocked { get; set; }
    public string Orientation { get; set; } = "Horizontal";
}

public interface ISessionStore
{
    Task<SessionState?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(SessionState state, CancellationToken cancellationToken = default);
}
