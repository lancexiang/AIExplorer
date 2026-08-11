using System.Collections.Concurrent;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AIExplorer_App.Services;

/// <summary>
/// GitLab / VS Code Material Icon Theme 风格的文件类型图标（Assets/FileIcons，MIT）。
/// </summary>
public static class FileTypeIconService
{
    private static readonly ConcurrentDictionary<string, SvgImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AvailableAssets = DiscoverAssets();

    private static HashSet<string> DiscoverAssets()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "FileIcons");
            if (!Directory.Exists(dir))
            {
                return set;
            }

            foreach (var path in Directory.EnumerateFiles(dir, "*.svg"))
            {
                set.Add(Path.GetFileNameWithoutExtension(path));
            }
        }
        catch
        {
            // ignore — Resolve 会返回 null，退回 FontIcon / Shell
        }

        return set;
    }

    /// <summary>按名称/是否目录解析图标资源名（不含扩展名）。</summary>
    public static string? ResolveAssetKey(string fileName, bool isDirectory)
    {
        if (isDirectory)
        {
            return Available("folder");
        }

        var name = Path.GetFileName(fileName);
        if (string.IsNullOrEmpty(name))
        {
            return Available("document");
        }

        // 特殊文件名（无扩展名或约定名）
        var lower = name.ToLowerInvariant();
        var special = lower switch
        {
            "dockerfile" or "dockerfile.dev" or "dockerfile.prod" => "docker",
            "docker-compose.yml" or "docker-compose.yaml" or "compose.yml" or "compose.yaml" => "docker",
            "package.json" => "npm",
            "package-lock.json" => "npm",
            "yarn.lock" => "yarn",
            "cmakelists.txt" => "cmake",
            "makefile" or "gnumakefile" => "settings",
            // README 用 Markdown「M」图标（与 GitLab/VS Code 一致），不用 info 圆标
            "readme" or "readme.md" or "readme.txt" or "readme.rst" => "markdown",
            "license" or "license.md" or "license.txt" or "licence" => "license",
            "robots.txt" => "robots",
            ".gitignore" or ".gitattributes" or ".gitmodules" => "git",
            ".editorconfig" => "settings",
            ".env" or ".env.local" or ".env.development" or ".env.production" => "tune",
            _ => null,
        };
        if (special is not null)
        {
            return Available(special);
        }

        var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0)
        {
            return Available("document");
        }

        var key = ExtToAsset(ext);
        return Available(key) ?? Available("document");
    }

    public static ImageSource? GetImageSource(string fileName, bool isDirectory)
    {
        var key = ResolveAssetKey(fileName, isDirectory);
        if (key is null)
        {
            return null;
        }

        return Cache.GetOrAdd(key, static k =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "FileIcons", k + ".svg");
            var source = new SvgImageSource(new Uri(path));
            source.RasterizePixelWidth = 32;
            source.RasterizePixelHeight = 32;
            return source;
        });
    }

    /// <summary>已知扩展名优先用 Material 图标，不走 Shell（.exe/.lnk 等仍走 Shell）。</summary>
    public static bool PreferMaterialOverShell(string fileName, bool isDirectory)
    {
        if (isDirectory)
        {
            return Available("folder") is not null;
        }

        // exe/lnk 等保留系统关联图标；.bat/.cmd 用 Material console（橙色终端）
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is not (".exe" or ".lnk" or ".url" or ".msi" or ".com" or ".scr");
    }

    private static string? Available(string? key)
    {
        if (key is null)
        {
            return null;
        }

        return AvailableAssets.Contains(key) ? key : null;
    }

    private static string ExtToAsset(string ext) => ext switch
    {
        "json" or "jsonc" or "json5" => "json",
        "xml" or "xaml" or "xsl" or "xslt" or "xsd" or "plist" => "xml",
        "yml" or "yaml" => "yaml",
        "py" or "pyw" or "pyi" or "ipynb" => "python",
        "cs" or "csx" => "csharp",
        "c" => "c",
        "cpp" or "cc" or "cxx" or "c++" => "cpp",
        "h" or "hh" or "hpp" or "hxx" => "h",
        "java" or "jar" or "class" => "java",
        "js" or "mjs" or "cjs" => "javascript",
        "ts" or "mts" or "cts" => "typescript",
        "tsx" or "jsx" => "react",
        "html" or "htm" => "html",
        "css" => "css",
        "scss" or "sass" => "sass",
        "vue" => "vue",
        "go" => "go",
        "rs" => "rust",
        "rb" or "erb" => "ruby",
        "php" => "php",
        "swift" => "swift",
        "kt" or "kts" => "kotlin",
        "dart" => "dart",
        "md" or "markdown" or "mdown" => "markdown",
        "mdx" => "mdx",
        "pdf" => "pdf",
        "doc" or "docx" or "rtf" or "odt" => "word",
        "xls" or "xlsx" or "xlsm" or "ods" => "excel",
        "csv" or "tsv" => "csv",
        "ppt" or "pptx" or "odp" => "powerpoint",
        "zip" or "rar" or "7z" or "tar" or "gz" or "tgz" or "bz2" or "xz" or "cab" => "zip",
        "png" or "jpg" or "jpeg" or "gif" or "bmp" or "webp" or "ico" or "tif" or "tiff" or "heic" or "avif" => "image",
        "svg" => "svg",
        "mp4" or "mkv" or "avi" or "mov" or "wmv" or "webm" or "m4v" => "video",
        "mp3" or "wav" or "flac" or "m4a" or "aac" or "ogg" or "wma" => "audio",
        "ttf" or "otf" or "woff" or "woff2" or "eot" => "font",
        "dll" or "so" or "dylib" => "dll",
        "lib" or "a" => "lib",
        "obj" or "o" => "object",
        "pdb" or "ilk" or "exp" or "lastbuildstate" or "tlog" => "settings",
        "db" or "sqlite" or "sqlite3" or "mdb" or "accdb" => "database",
        "sql" => "sql",
        "log" => "log",
        "ini" or "cfg" or "conf" or "config" or "props" or "targets" or "editorconfig" => "ini",
        "toml" => "toml",
        "env" => "tune",
        "diff" or "patch" => "diff",
        "lock" => "lock",
        "pem" or "crt" or "cer" or "p12" or "pfx" => "certificate",
        "key" or "pub" => "key",
        "ps1" or "psm1" or "psd1" => "powershell",
        "sh" or "bash" or "zsh" or "fish" => "bash",
        "bat" or "cmd" => "console",
        "proto" => "proto",
        "graphql" or "gql" => "graphql",
        "dockerfile" => "docker",
        "cmake" => "cmake",
        "gradle" or "gradlew" => "gradle",
        "sln" or "csproj" or "vbproj" or "fsproj" or "vcxproj" or "filters" => "settings",
        "txt" or "text" => "document",
        "bin" or "dat" or "raw" => "binary",
        "hex" => "hex",
        "fig" => "figma",
        "blend" => "blender",
        "unity" or "unitypackage" => "unity",
        "gd" or "tscn" or "godot" => "godot",
        "hlsl" or "glsl" or "shader" or "cginc" or "fx" => "shader",
        "tex" or "latex" or "bib" => "tex",
        "m" or "matlab" => "matlab",
        "f" or "f90" or "f95" or "for" => "fortran",
        "lua" => "lua",
        "pl" or "pm" => "perl",
        "r" or "rmd" => "r",
        "scala" or "sc" => "scala",
        "zig" => "zig",
        "hs" or "lhs" => "haskell",
        "ex" or "exs" => "elixir",
        "clj" or "cljs" or "cljc" => "clojure",
        "fs" or "fsi" or "fsx" => "fsharp",
        "url" or "webloc" => "url",
        "eml" or "msg" => "email",
        "todo" => "todo",
        _ => "document",
    };
}
