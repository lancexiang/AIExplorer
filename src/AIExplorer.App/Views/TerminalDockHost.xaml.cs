using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AIExplorer_App.Views;

/// <summary>
/// 多会话终端 Dock：同目录再开会聚焦已有标签；新目录默认新建；可重命名 / 多开。
/// </summary>
public sealed partial class TerminalDockHost : UserControl
{
    private sealed class Session
    {
        public required string Id { get; init; }
        public required string WorkingDirectory { get; set; }
        public required string Title { get; set; }
        public bool TitleCustomized { get; set; }
        public required TerminalPane Pane { get; init; }
        public required Border TabChrome { get; init; }
        public required TextBlock TitleBlock { get; init; }
    }

    private readonly List<Session> _sessions = [];
    private Session? _active;
    private string? _preferredDirectory;

    /// <summary>工具栏「当前文件夹」提供者，用于 + 新建。</summary>
    public Func<string?>? ActiveDirectoryProvider { get; set; }

    public event Action? CollapseRequested;

    public TerminalDockHost()
    {
        InitializeComponent();
    }

    public int SessionCount => _sessions.Count;

    public string? ActiveWorkingDirectory => _active?.WorkingDirectory;

    /// <summary>
    /// 打开或聚焦终端：已有同目录会话则选中；否则新建。
    /// </summary>
    public async Task OpenOrFocusAsync(string? workingDirectory, bool forceNew = false)
    {
        var cwd = NormalizeDir(workingDirectory);
        _preferredDirectory = cwd;

        if (!forceNew)
        {
            var existing = FindByDirectory(cwd);
            if (existing is not null)
            {
                SelectSession(existing);
                await existing.Pane.EnsureStartedAsync(cwd);
                return;
            }
        }

        await CreateSessionAsync(cwd);
    }

    public void DisposeAll()
    {
        foreach (var s in _sessions.ToArray())
        {
            CloseSession(s, notifyEmpty: false);
        }

        _active = null;
    }

    private async void OnAddClick(object sender, RoutedEventArgs e)
    {
        var fromExplorer = ActiveDirectoryProvider?.Invoke();
        var cwd = NormalizeDir(fromExplorer ?? _preferredDirectory ?? _active?.WorkingDirectory);
        await CreateSessionAsync(cwd, forceUniqueTitle: true);
    }

    private void OnRestartClick(object sender, RoutedEventArgs e) =>
        _active?.Pane.Restart(_active.WorkingDirectory);

    private void OnClearClick(object sender, RoutedEventArgs e) =>
        _active?.Pane.ClearScreen();

    private void OnCollapseClick(object sender, RoutedEventArgs e) =>
        CollapseRequested?.Invoke();

    private async Task CreateSessionAsync(string cwd, bool forceUniqueTitle = false)
    {
        var id = Guid.NewGuid().ToString("N");
        var title = BuildDefaultTitle(cwd, forceUniqueTitle);
        var pane = new TerminalPane();

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 160,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var closeIcon = new FontIcon { Glyph = "\uE711", FontSize = 10 };
        var closeBtn = new Button
        {
            Content = closeIcon,
            Style = (Style)Application.Current.Resources["AppToolButtonStyle"],
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(closeBtn, "关闭终端");

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(titleBlock);
        header.Children.Add(closeBtn);

        var tab = new Border
        {
            Child = header,
            Padding = new Thickness(10, 4, 6, 4),
            Margin = new Thickness(0, 0, 2, 0),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0, 0, 0)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(tab, cwd);

        var session = new Session
        {
            Id = id,
            WorkingDirectory = cwd,
            Title = title,
            Pane = pane,
            TabChrome = tab,
            TitleBlock = titleBlock,
        };

        tab.PointerPressed += (_, args) =>
        {
            if (args.GetCurrentPoint(tab).Properties.IsLeftButtonPressed)
            {
                SelectSession(session);
            }
        };
        tab.RightTapped += async (_, args) =>
        {
            args.Handled = true;
            await RenameSessionAsync(session);
        };
        tab.DoubleTapped += async (_, args) =>
        {
            args.Handled = true;
            await RenameSessionAsync(session);
        };
        closeBtn.Click += (_, _) => CloseSession(session, notifyEmpty: true);

        _sessions.Add(session);
        TabStrip.Children.Add(tab);
        PaneHost.Children.Add(pane);
        pane.Visibility = Visibility.Collapsed;

        SelectSession(session);
        await pane.EnsureStartedAsync(cwd);
    }

    private void SelectSession(Session session)
    {
        _active = session;
        foreach (var s in _sessions)
        {
            var selected = ReferenceEquals(s, session);
            s.Pane.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            s.TabChrome.Background = selected
                ? (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0, 0, 0));
        }

        _ = session.Pane.FocusTerminalAsync();
    }

    private void CloseSession(Session session, bool notifyEmpty)
    {
        var index = _sessions.IndexOf(session);
        if (index < 0)
        {
            return;
        }

        _sessions.RemoveAt(index);
        TabStrip.Children.Remove(session.TabChrome);
        PaneHost.Children.Remove(session.Pane);
        session.Pane.Dispose();

        if (_sessions.Count == 0)
        {
            _active = null;
            if (notifyEmpty)
            {
                CollapseRequested?.Invoke();
            }

            return;
        }

        if (ReferenceEquals(_active, session))
        {
            var next = _sessions[Math.Clamp(index, 0, _sessions.Count - 1)];
            SelectSession(next);
        }
    }

    private async Task RenameSessionAsync(Session session)
    {
        var box = new TextBox
        {
            Text = session.Title,
            SelectionStart = 0,
            SelectionLength = session.Title.Length,
        };

        var dialog = new ContentDialog
        {
            Title = "重命名终端",
            Content = box,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        var name = (box.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        session.Title = name;
        session.TitleCustomized = true;
        session.TitleBlock.Text = name;
    }

    private Session? FindByDirectory(string cwd) =>
        _sessions.FirstOrDefault(s => PathsEqual(s.WorkingDirectory, cwd));

    private string BuildDefaultTitle(string cwd, bool forceUnique)
    {
        var name = string.IsNullOrWhiteSpace(cwd)
            ? "终端"
            : (Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } n
                ? n
                : cwd);

        if (!forceUnique && _sessions.All(s => !string.Equals(s.Title, name, StringComparison.OrdinalIgnoreCase)))
        {
            return name;
        }

        var baseName = name;
        var i = 2;
        while (_sessions.Any(s => string.Equals(s.Title, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{baseName} ({i++})";
        }

        return name;
    }

    private static string NormalizeDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path;
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    public void SetPreferredDirectory(string? path) =>
        _preferredDirectory = NormalizeDir(path);
}
