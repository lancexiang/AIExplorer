namespace AIExplorer.Core.Files;

/// <summary>UNC 路径拼接（避免 Path.Combine 对 \\server\share 的边缘问题）。</summary>
public static class UncPathHelper
{
    public static string Join(string uncRoot, string? relativeSuffix)
    {
        var root = (uncRoot ?? string.Empty).TrimEnd('\\', '/');
        var suffix = (relativeSuffix ?? string.Empty).TrimStart('\\', '/');
        return suffix.Length == 0 ? root : root + "\\" + suffix;
    }

    /// <summary>
    /// 把 GetFinalPathNameByHandle 等返回的 \\?\UNC\server\share 规范化为 \\server\share。
    /// </summary>
    public static string NormalizeExtendedUnc(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[@"\\?\UNC\".Length..];
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path[@"\\?\".Length..];
        }

        return path;
    }
}
