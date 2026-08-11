using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace AIExplorer_App.Views;

/// <summary>业务 Tab 蓝头/标题的共享绘制（左栏与 PaneTabHost 同构）。</summary>
internal static class TabChromeHelper
{
    private static Brush? Accent;
    private static Brush? ActiveFg;
    private static Brush? InactiveFg;
    private static Brush? InactiveIconFg;
    private static Brush? TransparentBrush;

    public static void EnsureBrushes()
    {
        if (Accent is not null)
        {
            return;
        }

        Accent = Application.Current.Resources.TryGetValue("AppAccentBrush", out var a) && a is Brush ab
            ? ab
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 212));
        ActiveFg = new SolidColorBrush(Microsoft.UI.Colors.White);
        InactiveFg = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32));
        InactiveIconFg = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 96, 96, 96));
        TransparentBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    public static void SetActiveVisual(TabViewItem item, bool active)
    {
        EnsureBrushes();
        item.IsClosable = false;
        item.Background = TransparentBrush;

        if (item.Header is not Border root || !Equals(root.Tag, "TabHeaderRoot"))
        {
            return;
        }

        root.Background = active ? Accent : TransparentBrush;
        ApplyForeground(
            root.Child as DependencyObject,
            active ? ActiveFg! : InactiveFg!,
            active ? ActiveFg! : InactiveIconFg!);
    }

    public static FrameworkElement CreateFilePaneHeader(string title, bool canClose, Action? onClose)
    {
        EnsureBrushes();
        var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(new FontIcon
        {
            Glyph = "\uE8B7",
            FontSize = 12,
            Foreground = InactiveIconFg,
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Tag = "Title",
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = InactiveFg,
        });
        Grid.SetColumn(panel, 0);
        row.Children.Add(panel);

        var close = new Button
        {
            Tag = "TabClose",
            Content = new FontIcon
            {
                Glyph = "\uE711",
                FontSize = 10,
                Foreground = InactiveFg,
            },
            Width = 22,
            Height = 22,
            MinWidth = 22,
            MinHeight = 22,
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = TransparentBrush,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
        };
        if (onClose is not null)
        {
            close.Click += (_, _) => onClose();
        }

        if (!canClose)
        {
            close.Opacity = 0;
            close.IsHitTestVisible = false;
            close.IsEnabled = false;
        }

        Grid.SetColumn(close, 1);
        row.Children.Add(close);

        return WrapHeader(row);
    }

    public static Border WrapHeader(FrameworkElement inner)
    {
        EnsureBrushes();
        // 必须与 MainPage.WrapTabHeader 完全一致，否则分栏处标签底线/地址栏顶线会错位成台阶
        return new Border
        {
            Tag = "TabHeaderRoot",
            Child = inner,
            Background = TransparentBrush,
            Padding = new Thickness(12, 5, 10, 5),
            Margin = new Thickness(-8, -4, -8, -4),
            CornerRadius = new CornerRadius(4),
            MinHeight = 30,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
    }

    public static TextBlock? FindTitleText(object? header)
    {
        switch (header)
        {
            case TextBlock { Tag: "Title" } t:
                return t;
            case Border { Child: { } child }:
                return FindTitleText(child);
            case Panel panel:
                foreach (var child in panel.Children)
                {
                    if (FindTitleText(child) is { } found)
                    {
                        return found;
                    }
                }

                break;
        }

        return null;
    }

    public static Button? FindCloseButton(object? header)
    {
        switch (header)
        {
            case Button { Tag: "TabClose" } b:
                return b;
            case Border { Child: { } child }:
                return FindCloseButton(child);
            case Panel panel:
                foreach (var child in panel.Children)
                {
                    if (FindCloseButton(child) is { } found)
                    {
                        return found;
                    }
                }

                break;
        }

        return null;
    }

    public static bool IsUnderCloseButton(DependencyObject? start)
    {
        for (var cur = start; cur is not null; cur = VisualTreeHelper.GetParent(cur))
        {
            if (cur is Button { Tag: "TabClose" })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 关掉标签条入场/重排动画。否则 + 新建日志很短，视觉上会先留白再上蓝。
    /// </summary>
    public static void KillTabStripTransitions(TabView tabView)
    {
        if (FindDescendant<ListView>(tabView) is { } listView)
        {
            listView.ItemContainerTransitions = [];
        }

        if (FindDescendant<ItemsControl>(tabView) is { } items &&
            !ReferenceEquals(items, tabView))
        {
            items.ItemContainerTransitions = [];
        }

        tabView.Transitions = [];
    }

    public static void NormalizeTabStrip(TabView tabView, double height = 44)
    {
        var presenter = FindDescendant<ContentPresenter>(tabView, "TabContentPresenter");
        if (presenter is not null)
        {
            presenter.Visibility = Visibility.Collapsed;
            presenter.Height = 0;
            presenter.MaxHeight = 0;
            presenter.MinHeight = 0;
        }

        tabView.Padding = new Thickness(0);
        tabView.Margin = new Thickness(0);
        tabView.Height = height;
        tabView.MaxHeight = height;
        tabView.MinHeight = height;
        tabView.VerticalAlignment = VerticalAlignment.Stretch;

        if (FindDescendant<FrameworkElement>(tabView, "TabContainerGrid") is { } strip)
        {
            strip.Margin = new Thickness(0);
            strip.VerticalAlignment = VerticalAlignment.Stretch;
            if (strip is Control stripControl)
            {
                stripControl.Padding = new Thickness(0);
            }
        }

        if (FindDescendant<ListView>(tabView) is { } list)
        {
            list.Padding = new Thickness(0);
            list.Margin = new Thickness(0);
            list.VerticalAlignment = VerticalAlignment.Stretch;
            list.ItemContainerTransitions = [];
        }
    }

    public static T? FindDescendant<T>(DependencyObject root, string? name = null)
        where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && (name is null || match.Name == name))
            {
                return match;
            }

            var nested = FindDescendant<T>(child, name);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static void ApplyForeground(DependencyObject? node, Brush fg, Brush iconFg)
    {
        switch (node)
        {
            case null:
                return;
            case TextBlock text:
                text.Foreground = fg;
                return;
            case FontIcon icon:
                icon.Foreground = iconFg;
                return;
            case Button { Tag: "TabClose" } closeBtn:
                ApplyForeground(closeBtn.Content as DependencyObject, fg, iconFg);
                return;
            case Panel panel:
                foreach (var child in panel.Children)
                {
                    ApplyForeground(child as DependencyObject, fg, iconFg);
                }

                return;
            case Border { Child: DependencyObject borderChild }:
                ApplyForeground(borderChild, fg, iconFg);
                return;
            case ContentControl { Content: DependencyObject content }:
                ApplyForeground(content, fg, iconFg);
                return;
        }
    }
}
