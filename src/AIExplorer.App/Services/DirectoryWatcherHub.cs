namespace AIExplorer_App.Services;

/// <summary>
/// 同路径多窗格共用一个 <see cref="FileSystemWatcher"/>。
/// 网络盘上每建一个 Watcher / 同步 Directory.Exists 都很贵；+ 多开时尤其明显。
/// </summary>
internal static class DirectoryWatcherHub
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry
    {
        public required FileSystemWatcher Watcher { get; init; }
        public readonly List<Subscription> Subs = [];
    }

    private sealed class Subscription : IDisposable
    {
        public required string Key;
        public required Action<WatcherChangeTypes, string> OnChange;
        public required Action<string, string> OnRename;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Unsubscribe(this);
        }
    }

    /// <summary>
    /// 订阅目录变更。不预先 Directory.Exists（UNC 会卡住 UI）；构造失败则返回 null。
    /// </summary>
    public static IDisposable? TrySubscribe(
        string path,
        Action<WatcherChangeTypes, string> onChange,
        Action<string, string> onRename)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var key = NormalizeKey(path);
        lock (Gate)
        {
            if (!Entries.TryGetValue(key, out var entry))
            {
                try
                {
                    var watcher = new FileSystemWatcher(key)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                        IncludeSubdirectories = false,
                        EnableRaisingEvents = true,
                    };

                    entry = new Entry { Watcher = watcher };
                    watcher.Created += (_, e) => FanoutChange(key, e);
                    watcher.Deleted += (_, e) => FanoutChange(key, e);
                    watcher.Renamed += (_, e) => FanoutRename(key, e);
                    Entries[key] = entry;
                }
                catch
                {
                    return null;
                }
            }

            var sub = new Subscription
            {
                Key = key,
                OnChange = onChange,
                OnRename = onRename,
            };
            entry.Subs.Add(sub);
            return sub;
        }
    }

    private static void FanoutChange(string key, FileSystemEventArgs e)
    {
        if (e.ChangeType is not (WatcherChangeTypes.Created or WatcherChangeTypes.Deleted))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(e.FullPath))
        {
            return;
        }

        Subscription[] snapshot;
        lock (Gate)
        {
            if (!Entries.TryGetValue(key, out var entry) || entry.Subs.Count == 0)
            {
                return;
            }

            snapshot = entry.Subs.ToArray();
        }

        foreach (var sub in snapshot)
        {
            try
            {
                sub.OnChange(e.ChangeType, e.FullPath);
            }
            catch
            {
                // 单个订阅方失败不影响其它窗格
            }
        }
    }

    private static void FanoutRename(string key, RenamedEventArgs e)
    {
        Subscription[] snapshot;
        lock (Gate)
        {
            if (!Entries.TryGetValue(key, out var entry) || entry.Subs.Count == 0)
            {
                return;
            }

            snapshot = entry.Subs.ToArray();
        }

        foreach (var sub in snapshot)
        {
            try
            {
                sub.OnRename(e.OldFullPath, e.FullPath);
            }
            catch
            {
            }
        }
    }

    private static void Unsubscribe(Subscription sub)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue(sub.Key, out var entry))
            {
                return;
            }

            entry.Subs.Remove(sub);
            if (entry.Subs.Count > 0)
            {
                return;
            }

            Entries.Remove(sub.Key);
            try
            {
                entry.Watcher.EnableRaisingEvents = false;
                entry.Watcher.Dispose();
            }
            catch
            {
            }
        }
    }

    private static string NormalizeKey(string path)
    {
        var trimmed = path.Trim().TrimEnd('\\');
        // UNC 上 GetFullPath / Exists 会同步卡 UI；key 只做规范化字符串即可
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return trimmed;
        }

        try
        {
            return Path.GetFullPath(trimmed).TrimEnd('\\');
        }
        catch
        {
            return trimmed;
        }
    }
}
