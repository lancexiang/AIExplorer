using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AIExplorer.Core.Files;

public enum DriveMediaKind
{
    Unknown,
    Hdd,
    Ssd,
    Removable,
    Optical,
    Network,
    Ram,
}

/// <summary>探测盘符介质类型（HDD / SSD），供导航栏图标使用。</summary>
public static class DriveMediaDetector
{
    public static DriveMediaKind Detect(DriveInfo drive)
    {
        try
        {
            return drive.DriveType switch
            {
                DriveType.Removable => DriveMediaKind.Removable,
                DriveType.CDRom => DriveMediaKind.Optical,
                DriveType.Network => DriveMediaKind.Network,
                DriveType.Ram => DriveMediaKind.Ram,
                _ => DetectFixed(drive.Name),
            };
        }
        catch
        {
            return DriveMediaKind.Unknown;
        }
    }

    public static string Glyph(DriveMediaKind kind) => kind switch
    {
        DriveMediaKind.Ssd => "\uEDA2",       // HardDrive（实心，偏 SSD）
        DriveMediaKind.Hdd => "\uE7F4",       // HardDrive / 旋转盘观感
        DriveMediaKind.Removable => "\uE88E",
        DriveMediaKind.Optical => "\uE958",
        DriveMediaKind.Network => "\uE968",
        DriveMediaKind.Ram => "\uE964",
        _ => "\uEDA2",
    };

    public static string Label(DriveMediaKind kind) => kind switch
    {
        DriveMediaKind.Ssd => "SSD",
        DriveMediaKind.Hdd => "HDD",
        DriveMediaKind.Removable => "可移动",
        DriveMediaKind.Optical => "光盘",
        DriveMediaKind.Network => "网络",
        DriveMediaKind.Ram => "RAM",
        _ => "磁盘",
    };

    private static DriveMediaKind DetectFixed(string driveRoot)
    {
        try
        {
            var root = driveRoot.TrimEnd('\\') + "\\";
            using var handle = CreateFileW(
                @"\\.\" + root.TrimEnd('\\'),
                0,
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                return DriveMediaKind.Unknown;
            }

            var query = new STORAGE_PROPERTY_QUERY
            {
                PropertyId = StorageDeviceSeekPenaltyProperty,
                QueryType = PropertyStandardQuery,
            };

            var desc = new DEVICE_SEEK_PENALTY_DESCRIPTOR();
            var size = (uint)Marshal.SizeOf<DEVICE_SEEK_PENALTY_DESCRIPTOR>();
            if (!DeviceIoControl(
                    handle,
                    IOCTL_STORAGE_QUERY_PROPERTY,
                    ref query,
                    (uint)Marshal.SizeOf<STORAGE_PROPERTY_QUERY>(),
                    ref desc,
                    size,
                    out _,
                    IntPtr.Zero))
            {
                return DriveMediaKind.Unknown;
            }

            return desc.IncursSeekPenalty != 0 ? DriveMediaKind.Hdd : DriveMediaKind.Ssd;
        }
        catch
        {
            return DriveMediaKind.Unknown;
        }
    }

    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint StorageDeviceSeekPenaltyProperty = 7;
    private const uint PropertyStandardQuery = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public uint PropertyId;
        public uint QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_SEEK_PENALTY_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        public byte IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref STORAGE_PROPERTY_QUERY lpInBuffer,
        uint nInBufferSize,
        ref DEVICE_SEEK_PENALTY_DESCRIPTOR lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
