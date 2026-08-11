using System.Runtime.CompilerServices;
using AIExplorer.Core.Files;

namespace AIExplorer.Infrastructure.Files;

/// <summary>系统目录枚举：仅 FileInfo/DirectoryInfo stat，不打开文件内容。</summary>
public sealed class ShellFileListSource : IFileListSource
{
    public async IAsyncEnumerable<FileEntrySnapshot> EnumerateIncrementalAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(path))
        {
            yield break;
        }

        // Dirs first, then files. ConfigureAwait(true) resumes on caller context when possible.
        var dirs = await Task.Run(() => EnumerateDirectories(path), cancellationToken)
            .ConfigureAwait(true);
        foreach (var entry in dirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }

        var files = await Task.Run(() => EnumerateFiles(path), cancellationToken)
            .ConfigureAwait(true);
        foreach (var entry in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }
    }

    public string? GetParentPath(string path) => Directory.GetParent(path)?.FullName;

    private static List<FileEntrySnapshot> EnumerateDirectories(string path)
    {
        var result = new List<FileEntrySnapshot>();
        try
        {
            foreach (var child in new DirectoryInfo(path).EnumerateDirectories())
            {
                if ((child.Attributes & FileAttributes.Hidden) != 0)
                {
                    continue;
                }

                result.Add(new FileEntrySnapshot
                {
                    Stat = new FileStat
                    {
                        Name = child.Name,
                        FullPath = child.FullName,
                        IsDirectory = true,
                        ModifiedTime = child.LastWriteTimeUtc,
                    },
                });
            }
        }
        catch (UnauthorizedAccessException)
        {
        }

        result.Sort(static (a, b) =>
            string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static List<FileEntrySnapshot> EnumerateFiles(string path)
    {
        var result = new List<FileEntrySnapshot>();
        try
        {
            foreach (var file in new DirectoryInfo(path).EnumerateFiles())
            {
                if ((file.Attributes & FileAttributes.Hidden) != 0)
                {
                    continue;
                }

                result.Add(new FileEntrySnapshot
                {
                    Stat = new FileStat
                    {
                        Name = file.Name,
                        FullPath = file.FullName,
                        IsDirectory = false,
                        Size = file.Length,
                        ModifiedTime = file.LastWriteTimeUtc,
                    },
                });
            }
        }
        catch (UnauthorizedAccessException)
        {
        }

        result.Sort(static (a, b) =>
            string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }
}

public sealed class ShellFileSystemService : IFileSystemService
{
    private const int MaxUndoOperations = 3;
    private readonly string _undoCacheRoot;
    private readonly LinkedList<DeleteOperation> _undoOperations = [];
    private readonly SemaphoreSlim _undoGate = new(1, 1);

    public ShellFileSystemService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIExplorer",
            "DeleteUndo"))
    {
    }

    public ShellFileSystemService(string undoCacheRoot)
    {
        _undoCacheRoot = undoCacheRoot;
        Directory.CreateDirectory(_undoCacheRoot);
    }

    public bool CanUndoDelete => _undoOperations.Count > 0;

    public Task OpenPathAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.CompletedTask;
        }

        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"'));
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = full,
                UseShellExecute = true,
            };

            // .bat/.cmd/相对脚本依赖当前目录；默认会落到 AIExplorer 安装目录导致找不到 setup.py 等
            if (File.Exists(full))
            {
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    psi.WorkingDirectory = dir;
                }
            }
            else if (Directory.Exists(full))
            {
                psi.WorkingDirectory = full;
            }

            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> CopyOrMoveAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        bool move,
        CancellationToken cancellationToken = default)
    {
        var ops = new List<FileTransferOperation>();
        foreach (var source in sourcePaths)
        {
            var name = Path.GetFileName(source.TrimEnd('\\'));
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var dest = Path.Combine(destinationDirectory, name);
            dest = FilePathConflict.EnsureUniquePath(dest, Directory.Exists(source));
            ops.Add(new FileTransferOperation
            {
                SourcePath = source,
                DestinationPath = dest,
                Overwrite = false,
            });
        }

        return ExecuteTransferAsync(ops, move, cancellationToken);
    }

    public Task<IReadOnlyList<string>> ExecuteTransferAsync(
        IReadOnlyList<FileTransferOperation> operations,
        bool move,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var written = new List<string>();
            var errors = new List<string>();
            foreach (var op in operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var source = op.SourcePath;
                    var dest = op.DestinationPath;
                    if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(dest))
                    {
                        continue;
                    }

                    var destDir = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }

                    if (Directory.Exists(source))
                    {
                        TransferDirectory(source, dest, move, op.Overwrite);
                        written.Add(dest);
                    }
                    else if (File.Exists(source))
                    {
                        TransferFile(source, dest, move, op.Overwrite);
                        written.Add(dest);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(op.SourcePath)}: {ex.Message}");
                }
            }

            if (errors.Count > 0)
            {
                throw new IOException(string.Join(Environment.NewLine, errors));
            }

            return (IReadOnlyList<string>)written;
        }, cancellationToken);
    }

    private static void TransferFile(string source, string dest, bool move, bool overwrite)
    {
        if (overwrite && File.Exists(dest))
        {
            File.SetAttributes(dest, FileAttributes.Normal);
            File.Delete(dest);
        }

        if (move)
        {
            if (File.Exists(dest))
            {
                throw new IOException($"目标已存在：{Path.GetFileName(dest)}");
            }

            File.Move(source, dest, overwrite: false);
        }
        else
        {
            File.Copy(source, dest, overwrite: overwrite);
        }
    }

    private static void TransferDirectory(string source, string dest, bool move, bool overwrite)
    {
        var srcFull = Path.GetFullPath(source).TrimEnd('\\');
        var destFull = Path.GetFullPath(dest).TrimEnd('\\');
        if (destFull.Equals(srcFull, StringComparison.OrdinalIgnoreCase) ||
            destFull.StartsWith(srcFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("不能将文件夹移动/复制到其自身或其子目录中。");
        }

        if (Directory.Exists(dest))
        {
            if (!overwrite)
            {
                throw new IOException($"目标已存在：{Path.GetFileName(dest)}");
            }

            Directory.Delete(dest, recursive: true);
        }
        else if (File.Exists(dest))
        {
            if (!overwrite)
            {
                throw new IOException($"目标已存在：{Path.GetFileName(dest)}");
            }

            File.Delete(dest);
        }

        if (move)
        {
            Directory.Move(source, dest);
        }
        else
        {
            CopyDirectory(source, dest);
        }
    }

    public Task DeleteAsync(IReadOnlyList<string> paths, bool toRecycleBin = true, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            await _undoGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var operationFolder = Path.Combine(_undoCacheRoot, Guid.NewGuid().ToString("N"));
            var moved = new List<DeletedPath>();
            var errors = new List<string>();
            try
            {
                Directory.CreateDirectory(operationFolder);
                for (var index = 0; index < paths.Count; index++)
                {
                    var path = paths[index];
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var isDirectory = Directory.Exists(path);
                        if (!isDirectory && !File.Exists(path))
                        {
                            continue;
                        }

                        var name = Path.GetFileName(path.TrimEnd('\\'));
                        var cached = Path.Combine(operationFolder, $"{index:D4}_{name}");
                        MovePath(path, cached, isDirectory);
                        moved.Add(new DeletedPath(path, cached, isDirectory));
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                    }
                }

                if (moved.Count > 0)
                {
                    _undoOperations.AddLast(new DeleteOperation(operationFolder, moved));
                    while (_undoOperations.Count > MaxUndoOperations)
                    {
                        var oldest = _undoOperations.First!.Value;
                        _undoOperations.RemoveFirst();
                        TryDeleteDirectory(oldest.CacheFolder);
                    }
                }
                else
                {
                    TryDeleteDirectory(operationFolder);
                }
            }
            finally
            {
                _undoGate.Release();
            }

            if (errors.Count > 0)
            {
                throw new IOException(string.Join(Environment.NewLine, errors));
            }
        }, cancellationToken);
    }

    public async Task UndoLastDeleteAsync(CancellationToken cancellationToken = default)
    {
        await _undoGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_undoOperations.Last is null)
            {
                return;
            }

            var operation = _undoOperations.Last.Value;
            _undoOperations.RemoveLast();
            var errors = new List<string>();
            foreach (var item in operation.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!File.Exists(item.CachedPath) && !Directory.Exists(item.CachedPath))
                    {
                        continue;
                    }

                    var restorePath = FilePathConflict.EnsureUniquePath(item.OriginalPath, item.IsDirectory);
                    MovePath(item.CachedPath, restorePath, item.IsDirectory);
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(item.OriginalPath)}: {ex.Message}");
                }
            }

            TryDeleteDirectory(operation.CacheFolder);
            if (errors.Count > 0)
            {
                throw new IOException(string.Join(Environment.NewLine, errors));
            }
        }
        finally
        {
            _undoGate.Release();
        }
    }

    private static void MovePath(string source, string destination, bool isDirectory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            if (isDirectory)
            {
                Directory.Move(source, destination);
            }
            else
            {
                File.Move(source, destination);
            }
        }
        catch (IOException)
        {
            // 撤销缓存可能与源文件不在同一磁盘，跨卷时回退到复制后删除。
            if (isDirectory)
            {
                CopyDirectory(source, destination);
                Directory.Delete(source, recursive: true);
            }
            else
            {
                File.Copy(source, destination, overwrite: false);
                File.Delete(source);
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed record DeletedPath(string OriginalPath, string CachedPath, bool IsDirectory);

    private sealed record DeleteOperation(string CacheFolder, IReadOnlyList<DeletedPath> Items);

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }
}
