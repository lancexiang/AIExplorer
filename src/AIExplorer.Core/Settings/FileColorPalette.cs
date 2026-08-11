namespace AIExplorer.Core.Settings;

/// <summary>文件颜色标记定义（稳定 Key + 可配置显示名/色值/含义）。</summary>
public sealed class FileColorDefinition
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Hex { get; set; } = "#808080";
    public string Description { get; set; } = string.Empty;
}

public static class FileColorPalette
{
    public static IReadOnlyList<FileColorDefinition> Defaults { get; } =
    [
        new() { Key = "red", DisplayName = "红", Hex = "#E53935", Description = "进行中 / 紧急" },
        new() { Key = "orange", DisplayName = "橙", Hex = "#FB8C00", Description = "待跟进" },
        new() { Key = "yellow", DisplayName = "黄", Hex = "#FDD835", Description = "留意" },
        new() { Key = "green", DisplayName = "绿", Hex = "#43A047", Description = "已完成" },
        new() { Key = "blue", DisplayName = "蓝", Hex = "#1E88E5", Description = "资料 / 归档" },
    ];

    /// <summary>合并已保存配置与默认项：缺 key 补默认；非法项剔除。</summary>
    public static List<FileColorDefinition> MergeWithDefaults(IEnumerable<FileColorDefinition>? saved)
    {
        var byKey = new Dictionary<string, FileColorDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in Defaults)
        {
            byKey[d.Key] = Clone(d);
        }

        if (saved is not null)
        {
            foreach (var item in saved)
            {
                if (string.IsNullOrWhiteSpace(item.Key))
                {
                    continue;
                }

                byKey[item.Key] = new FileColorDefinition
                {
                    Key = item.Key.Trim(),
                    DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? item.Key : item.DisplayName.Trim(),
                    Hex = NormalizeHex(item.Hex) ?? byKey.GetValueOrDefault(item.Key)?.Hex ?? "#808080",
                    Description = item.Description?.Trim() ?? string.Empty,
                };
            }
        }

        // 保持默认顺序，其后追加自定义 key
        var result = new List<FileColorDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in Defaults)
        {
            result.Add(byKey[d.Key]);
            seen.Add(d.Key);
        }

        foreach (var pair in byKey)
        {
            if (seen.Add(pair.Key))
            {
                result.Add(pair.Value);
            }
        }

        return result;
    }

    public static FileColorDefinition? Find(IEnumerable<FileColorDefinition> palette, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return palette.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    public static string Tooltip(FileColorDefinition? def) =>
        def is null
            ? string.Empty
            : string.IsNullOrWhiteSpace(def.Description)
                ? def.DisplayName
                : $"{def.DisplayName} · {def.Description}";

    public static string? NormalizeHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }

        var s = hex.Trim();
        if (!s.StartsWith('#'))
        {
            s = "#" + s;
        }

        if (s.Length is not (4 or 7 or 9))
        {
            return null;
        }

        for (var i = 1; i < s.Length; i++)
        {
            if (!Uri.IsHexDigit(s[i]))
            {
                return null;
            }
        }

        return s.ToUpperInvariant();
    }

    public static bool TryParseRgb(string? hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        var n = NormalizeHex(hex);
        if (n is null)
        {
            return false;
        }

        try
        {
            if (n.Length == 4)
            {
                r = Convert.ToByte(new string(n[1], 2), 16);
                g = Convert.ToByte(new string(n[2], 2), 16);
                b = Convert.ToByte(new string(n[3], 2), 16);
                return true;
            }

            r = Convert.ToByte(n[1..3], 16);
            g = Convert.ToByte(n[3..5], 16);
            b = Convert.ToByte(n[5..7], 16);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static FileColorDefinition Clone(FileColorDefinition d) => new()
    {
        Key = d.Key,
        DisplayName = d.DisplayName,
        Hex = d.Hex,
        Description = d.Description,
    };
}
