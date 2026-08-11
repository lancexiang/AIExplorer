using System.Runtime.InteropServices;
using System.Text;
using AIExplorer.Core.Files;
using Microsoft.Win32.SafeHandles;

namespace AIExplorer_App;

/// <summary>把映射网络盘路径转换为稳定的 UNC 路径（优先 \\server\share 或 \\ip\share）。</summary>
internal static class NetworkPathResolver
{
    private const int NoError = 0;
    private const int ErrorMoreData = 234;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint VolumeNameDos = 0;

    public static string ToUncPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return UncPathHelper.NormalizeExtendedUnc(path);
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':')
        {
            return path;
        }

        var localName = root[..2];
        var suffix = path.Length > root.Length ? path[root.Length..] : string.Empty;

        if (TryGetMappedConnection(localName, out var remote) && !string.IsNullOrWhiteSpace(remote))
        {
            return UncPathHelper.Join(remote, suffix);
        }

        if (TryGetFinalPath(path, out var finalPath) &&
            finalPath.StartsWith(@"\\", StringComparison.Ordinal) &&
            !IsDriveLetterPath(finalPath))
        {
            return finalPath;
        }

        return path;
    }

    private static bool IsDriveLetterPath(string path) =>
        path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';

    private static bool TryGetMappedConnection(string localName, out string remoteName)
    {
        remoteName = string.Empty;
        var capacity = 512;
        var remote = new StringBuilder(capacity);
        var result = WNetGetConnection(localName, remote, ref capacity);
        if (result == ErrorMoreData)
        {
            remote = new StringBuilder(capacity);
            result = WNetGetConnection(localName, remote, ref capacity);
        }

        if (result != NoError || remote.Length == 0)
        {
            return false;
        }

        remoteName = remote.ToString();
        return true;
    }

    private static bool TryGetFinalPath(string path, out string uncPath)
    {
        uncPath = string.Empty;
        var handle = CreateFile(
            path,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            0,
            OpenExisting,
            FileFlagBackupSemantics,
            0);
        if (handle.IsInvalid)
        {
            return false;
        }

        try
        {
            var capacity = 512;
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)capacity, VolumeNameDos);
            if (length == 0)
            {
                return false;
            }

            if (length > capacity)
            {
                buffer.EnsureCapacity((int)length);
                length = GetFinalPathNameByHandle(handle, buffer, length, VolumeNameDos);
                if (length == 0)
                {
                    return false;
                }
            }

            uncPath = UncPathHelper.NormalizeExtendedUnc(buffer.ToString());
            return !string.IsNullOrWhiteSpace(uncPath);
        }
        finally
        {
            handle.Dispose();
        }
    }

    [DllImport("mpr.dll", EntryPoint = "WNetGetConnectionW", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(
        string localName,
        StringBuilder remoteName,
        ref int length);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle handle,
        StringBuilder filePath,
        uint filePathSize,
        uint flags);
}
