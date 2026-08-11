using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace AIExplorer.Setup;

internal sealed class InstallEngine
{
    public const string AppName = "AIExplorer";
    public const string DisplayName = "AI Explorer";
    public const string Publisher = "AIExplorer";
    public const string ProductVersion = "1.0.1";
    public const string UninstallRegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AIExplorer";

    private static readonly Version MinWarVersion = new(6000, 242, 101, 0);

    private static readonly string[] WarMsixOrder =
    [
        "Microsoft.WindowsAppRuntime.Main.1.6.msix",
        "Microsoft.WindowsAppRuntime.1.6.msix",
        "Microsoft.WindowsAppRuntime.Singleton.1.6.msix",
        "Microsoft.WindowsAppRuntime.DDLM.1.6.msix",
    ];

    private readonly StringBuilder _log = new();

    public string LogText => _log.ToString();

    public static string DefaultInstallDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            AppName);

    public static string FindPackageRoot()
    {
        var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            var dir = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrWhiteSpace(dir) && LooksLikePackageRoot(dir))
            {
                return dir;
            }
        }

        var baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (LooksLikePackageRoot(baseDir))
        {
            return baseDir;
        }

        throw new InvalidOperationException(
            "无法定位安装包目录。请把 AIExplorer-Setup.exe 与 app\\、dotnet\\、runtimes\\ 放在同一文件夹后再运行。");
    }

    private static bool LooksLikePackageRoot(string dir) =>
        File.Exists(Path.Combine(dir, "app", "AIExplorer.App.exe"));

    public void Install(
        string packageRoot,
        string installDir,
        bool createDesktopShortcut,
        bool createStartMenuShortcut,
        Action<int, string> progress)
    {
        void Step(int percent, string text)
        {
            Log(text);
            progress(percent, text);
        }

        var appSrc = Path.Combine(packageRoot, "app");
        var dotnetSrc = Path.Combine(packageRoot, "dotnet");
        var warDir = Path.Combine(packageRoot, "runtimes");
        var warMsixDir = Path.Combine(warDir, "war-msix");
        var warInstaller = Path.Combine(warDir, "WindowsAppRuntimeInstall-x64.exe");

        if (!File.Exists(Path.Combine(appSrc, "AIExplorer.App.exe")))
        {
            throw new InvalidOperationException("找不到 app\\AIExplorer.App.exe。");
        }

        KillApp();

        Step(5, "正在解除文件封锁（Mark of the Web）…");
        UnblockTree(packageRoot);

        Step(15, "正在部署 Windows App Runtime 1.6…");
        EnsureWindowsAppRuntime(warInstaller, warMsixDir);

        Step(45, "正在复制程序文件…");
        CopyDirectory(appSrc, installDir);

        Step(65, "正在复制私有 .NET 运行时…");
        CopyDirectory(dotnetSrc, Path.Combine(installDir, "dotnet"));

        Step(72, "正在复制 Windows App Runtime 安装器（供启动自愈）…");
        CopyWarRepairFiles(warDir, warMsixDir, installDir);

        Step(75, "正在写入启动器…");
        WriteLauncher(installDir);

        var launchVbs = Path.Combine(installDir, "AIExplorer.vbs");
        if (!File.Exists(Path.Combine(installDir, "AIExplorer.App.exe")) || !File.Exists(launchVbs))
        {
            throw new InvalidOperationException("安装后缺少 AIExplorer.App.exe 或 AIExplorer.vbs。");
        }

        // Keep a copy of Setup for ARP uninstall
        try
        {
            var setupSrc = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (File.Exists(setupSrc))
            {
                File.Copy(setupSrc, Path.Combine(installDir, "Uninstall.exe"), overwrite: true);
                File.Copy(setupSrc, Path.Combine(installDir, "AIExplorer-Setup.exe"), overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log("WARN copy uninstaller: " + ex.Message);
        }

        Step(85, "正在创建快捷方式…");
        if (createStartMenuShortcut)
        {
            CreateShortcut(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "AIExplorer.lnk"),
                launchVbs,
                installDir);
        }

        if (createDesktopShortcut)
        {
            CreateShortcut(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AIExplorer.lnk"),
                launchVbs,
                installDir);
        }

        Step(92, "正在注册卸载信息…");
        RegisterUninstall(installDir);

        if (!IsWarReady(out var warStatus))
        {
            throw new InvalidOperationException(
                "Windows App Runtime 1.6 仍未就绪。\n\n" + warStatus);
        }

        Step(100, "安装完成");
        Log("WAR OK: " + warStatus);
        Log("OK dest=" + installDir);
        File.WriteAllText(Path.Combine(installDir, "install.log"), _log.ToString(), Encoding.UTF8);
    }

    public void Uninstall(Action<int, string>? progress = null)
    {
        void Step(int percent, string text)
        {
            Log(text);
            progress?.Invoke(percent, text);
        }

        KillApp();

        var installDir = ReadInstallLocation() ?? DefaultInstallDir;
        Step(20, "正在删除快捷方式…");
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "AIExplorer.lnk"));
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AIExplorer.lnk"));

        Step(50, "正在删除程序文件…");
        if (Directory.Exists(installDir))
        {
            // Don't fail if Uninstall.exe is running from inside installDir — delete what we can
            try
            {
                foreach (var file in Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(file);
                    if (name.Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("AIExplorer-Setup.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try { File.Delete(file); } catch { /* ignore */ }
                }

                foreach (var dir in Directory.EnumerateDirectories(installDir).OrderByDescending(d => d.Length))
                {
                    try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
                }
            }
            catch (Exception ex)
            {
                Log("WARN delete files: " + ex.Message);
            }
        }

        Step(80, "正在移除卸载注册…");
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", writable: true);
            key?.DeleteSubKeyTree("AIExplorer", throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            Log("WARN remove ARP: " + ex.Message);
        }

        // Schedule remaining folder delete after exit (Uninstall.exe may lock itself)
        try
        {
            if (Directory.Exists(installDir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/C ping 127.0.0.1 -n 3 >nul & rmdir /S /Q \"{installDir}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
            }
        }
        catch
        {
            // ignore
        }

        Step(100, "卸载完成");
        // Note: do NOT remove Windows App Runtime — it is shared system component
    }

    public static string? ReadInstallLocation()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallRegKey);
            return key?.GetValue("InstallLocation") as string;
        }
        catch
        {
            return null;
        }
    }

    public static void LaunchApp(string installDir)
    {
        var exe = Path.Combine(installDir, "AIExplorer.App.exe");
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException("找不到 AIExplorer.App.exe", exe);
        }

        // 启动前再保一次 WAR，避免用户绕过 Setup 或运行时被卸载后弹系统对话框
        TryEnsureWarFromInstallDir(installDir);

        // Set DOTNET_ROOT in-process — no cmd/console flash
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = installDir,
            UseShellExecute = false,
        };
        psi.Environment["DOTNET_ROOT"] = Path.Combine(installDir, "dotnet");
        psi.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        Process.Start(psi);
    }

    private static void TryEnsureWarFromInstallDir(string installDir)
    {
        try
        {
            var engine = new InstallEngine();
            var warInstaller = Path.Combine(installDir, "runtimes", "WindowsAppRuntimeInstall-x64.exe");
            var warMsixDir = Path.Combine(installDir, "runtimes", "war-msix");
            if (File.Exists(warInstaller) || Directory.Exists(warMsixDir))
            {
                engine.EnsureWindowsAppRuntime(warInstaller, warMsixDir);
            }
        }
        catch
        {
            // Launch 仍继续；若 WAR 缺失会由系统弹窗引导
        }
    }

    private void RegisterUninstall(string installDir)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallRegKey)
            ?? throw new InvalidOperationException("无法写入卸载注册表。");

        var uninstallExe = Path.Combine(installDir, "Uninstall.exe");
        if (!File.Exists(uninstallExe))
        {
            uninstallExe = Path.Combine(installDir, "AIExplorer-Setup.exe");
        }

        key.SetValue("DisplayName", DisplayName);
        key.SetValue("DisplayVersion", ProductVersion);
        key.SetValue("Publisher", Publisher);
        key.SetValue("InstallLocation", installDir);
        key.SetValue("UninstallString", $"\"{uninstallExe}\" /uninstall");
        key.SetValue("QuietUninstallString", $"\"{uninstallExe}\" /uninstall /silent");
        key.SetValue("DisplayIcon", Path.Combine(installDir, "AIExplorer.App.exe"));
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", EstimateSizeKb(installDir), RegistryValueKind.DWord);
    }

    private static int EstimateSizeKb(string dir)
    {
        try
        {
            long bytes = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { bytes += new FileInfo(f).Length; } catch { /* ignore */ }
            }

            return (int)Math.Min(int.MaxValue, bytes / 1024);
        }
        catch
        {
            return 0;
        }
    }

    private void Log(string msg) =>
        _log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {msg}");

    private static void KillApp()
    {
        foreach (var p in Process.GetProcessesByName("AIExplorer.App"))
        {
            try { p.Kill(); } catch { /* ignore */ }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private static void UnblockTree(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try { File.Delete(file + ":Zone.Identifier"); } catch { /* ignore */ }
        }
    }

    private static void UnblockFile(string path)
    {
        try { File.Delete(path + ":Zone.Identifier"); } catch { /* ignore */ }
    }

    private void EnsureWindowsAppRuntime(string warInstaller, string warMsixDir)
    {
        if (IsWarReady(out var already))
        {
            Log("Already installed: " + already);
            return;
        }

        Log("Not ready: " + already);

        if (File.Exists(warInstaller))
        {
            UnblockFile(warInstaller);
            Log($"Run: {warInstaller} --quiet --force");
            var code = RunProcess(warInstaller, "--quiet --force", TimeSpan.FromMinutes(5));
            Log($"WindowsAppRuntimeInstall exit=0x{code:X8} ({code})");
            Thread.Sleep(1500);
            if (IsWarReady(out var afterInstaller))
            {
                Log("After official installer: " + afterInstaller);
                return;
            }

            // 当前用户安装失败时，尝试提权做整机部署（UAC 可能弹一次）
            Log("Per-user install insufficient — retry elevated…");
            var elevated = RunElevated(warInstaller, "--quiet --force", TimeSpan.FromMinutes(5));
            Log($"WindowsAppRuntimeInstall elevated exit=0x{elevated:X8} ({elevated})");
            Thread.Sleep(1500);
            if (IsWarReady(out var afterElevated))
            {
                Log("After elevated installer: " + afterElevated);
                return;
            }

            Log("Official installer did not register Main — force MSIX…");
        }

        if (!Directory.Exists(warMsixDir))
        {
            throw new InvalidOperationException("找不到 runtimes\\war-msix。");
        }

        foreach (var name in WarMsixOrder)
        {
            var path = Path.Combine(warMsixDir, name);
            if (!File.Exists(path))
            {
                Log("skip missing " + name);
                continue;
            }

            UnblockFile(path);
            Log("Add-AppxPackage -Path " + name);
            var (exit, stdout, stderr) = AddAppxPackage(path);
            Log($"  exit={exit} err={Trim(stderr)} out={Trim(stdout)}");
        }

        Thread.Sleep(1000);
        if (!IsWarReady(out var finalStatus))
        {
            throw new InvalidOperationException(
                "无法安装 Windows App Runtime 1.6（缺 Main 包）。\n\n" + finalStatus +
                "\n\n请以管理员身份重新运行 AIExplorer-Setup.exe，或手动运行 runtimes\\WindowsAppRuntimeInstall-x64.exe。");
        }

        Log("After MSIX: " + finalStatus);
    }

    private static void CopyWarRepairFiles(string warDir, string warMsixDir, string installDir)
    {
        var destWar = Path.Combine(installDir, "runtimes");
        Directory.CreateDirectory(destWar);

        var installer = Path.Combine(warDir, "WindowsAppRuntimeInstall-x64.exe");
        if (File.Exists(installer))
        {
            File.Copy(installer, Path.Combine(destWar, "WindowsAppRuntimeInstall-x64.exe"), overwrite: true);
            UnblockFile(Path.Combine(destWar, "WindowsAppRuntimeInstall-x64.exe"));
        }

        if (Directory.Exists(warMsixDir))
        {
            CopyDirectory(warMsixDir, Path.Combine(destWar, "war-msix"));
        }
    }

    private (int ExitCode, string StdOut, string StdErr) AddAppxPackage(string path)
    {
        var escaped = EscapePs(path);
        var attempts = new[]
        {
            $@"
$ErrorActionPreference = 'Stop'
Import-Module Appx -ErrorAction SilentlyContinue
Add-AppxPackage -Path '{escaped}' -ForceUpdateFromAnyVersion -ForceApplicationShutdown -ErrorAction Stop
",
            $@"
$ErrorActionPreference = 'Stop'
Import-Module Appx -ErrorAction SilentlyContinue
Add-AppxPackage -Path '{escaped}' -ForceApplicationShutdown -ErrorAction Stop
",
            $@"
$ErrorActionPreference = 'Stop'
Import-Module Appx -ErrorAction SilentlyContinue
Add-AppxPackage -Path '{escaped}' -ErrorAction Stop
",
        };

        (int, string, string) last = (-1, "", "");
        foreach (var ps in attempts)
        {
            last = RunPowerShell(ps, ignoreExitCode: true);
            if (last.Item1 == 0) return last;
        }

        return last;
    }

    private bool IsWarReady(out string status)
    {
        var ps = $@"
$min = [version]'{MinWarVersion}'
Import-Module Appx -ErrorAction SilentlyContinue
$all = @(Get-AppxPackage -ErrorAction SilentlyContinue)
$fw = @($all | Where-Object {{ $_.Name -eq 'Microsoft.WindowsAppRuntime.1.6' }})
$main = @($all | Where-Object {{ $_.Name -eq 'MicrosoftCorporationII.WinAppRuntime.Main.1.6' -or $_.Name -like '*WinAppRuntime.Main.1.6*' }})
$singleton = @($all | Where-Object {{ $_.Name -eq 'MicrosoftCorporationII.WinAppRuntime.Singleton' }})
$fwOk = @($fw | Where-Object {{ $_.Version -ge $min }})
$mainOk = @($main | Where-Object {{ $_.Version -ge $min }})
$parts = @()
$parts += ('fw_count=' + $fw.Count)
if ($fw) {{ $parts += ('fw_ver=' + (($fw | ForEach-Object {{ $_.Version.ToString() }}) -join ',')) }}
$parts += ('main_count=' + $main.Count)
if ($main) {{ $parts += ('main_ver=' + (($main | ForEach-Object {{ $_.Version.ToString() }}) -join ',')) }}
$parts += ('singleton_count=' + $singleton.Count)
Write-Output ($parts -join '; ')
if (($fwOk.Count -gt 0) -and ($mainOk.Count -gt 0)) {{ exit 0 }} else {{ exit 2 }}
";
        var (code, stdout, stderr) = RunPowerShell(ps, ignoreExitCode: true);
        status = string.IsNullOrWhiteSpace(stdout) ? $"check_exit={code} {Trim(stderr)}" : Trim(stdout);
        return code == 0;
    }

    private int RunProcess(string fileName, string arguments, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start " + fileName);
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(); } catch { /* ignore */ }
            throw new TimeoutException(fileName + " timed out");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (!string.IsNullOrWhiteSpace(stdout)) Log("stdout: " + Trim(stdout));
        if (!string.IsNullOrWhiteSpace(stderr)) Log("stderr: " + Trim(stderr));
        return p.ExitCode;
    }

    private int RunElevated(string fileName, string arguments, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                Log("elevated start returned null (UAC canceled?)");
                return -1;
            }

            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(); } catch { /* ignore */ }
                throw new TimeoutException(fileName + " elevated timed out");
            }

            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Log("elevated failed: " + ex.Message);
            return -1;
        }
    }

    private static void WriteLauncher(string dest)
    {
        // VBS: 启动前静默确保 WAR，再设 DOTNET_ROOT 启动 App（无控制台闪窗）
        var vbs = string.Join("\r\n", new[]
        {
            "Set sh = CreateObject(\"WScript.Shell\")",
            "Set fso = CreateObject(\"Scripting.FileSystemObject\")",
            "root = fso.GetParentFolderName(WScript.ScriptFullName)",
            "war = root & \"\\runtimes\\WindowsAppRuntimeInstall-x64.exe\"",
            "If fso.FileExists(war) Then",
            "  sh.Run \"\"\"\" & war & \"\"\" --quiet --force\", 0, True",
            "End If",
            "Set env = sh.Environment(\"Process\")",
            "env(\"DOTNET_ROOT\") = root & \"\\dotnet\"",
            "env(\"DOTNET_MULTILEVEL_LOOKUP\") = \"0\"",
            "sh.CurrentDirectory = root",
            "sh.Run \"\"\"\" & root & \"\\AIExplorer.App.exe\"\"\"\", 1, False",
        });
        File.WriteAllText(Path.Combine(dest, "AIExplorer.vbs"), vbs + "\r\n", Encoding.ASCII);

        // Keep .cmd as fallback for advanced users
        var cmd = string.Join("\r\n", new[]
        {
            "@echo off",
            "setlocal",
            "set \"ROOT=%~dp0\"",
            "if exist \"%ROOT%runtimes\\WindowsAppRuntimeInstall-x64.exe\" (",
            "  \"%ROOT%runtimes\\WindowsAppRuntimeInstall-x64.exe\" --quiet --force",
            ")",
            "set \"DOTNET_ROOT=%ROOT%dotnet\"",
            "set \"DOTNET_MULTILEVEL_LOOKUP=0\"",
            "start \"\" \"%ROOT%AIExplorer.App.exe\"",
        });
        File.WriteAllText(Path.Combine(dest, "AIExplorer.cmd"), cmd + "\r\n", Encoding.ASCII);
    }

    private static void CopyDirectory(string source, string dest)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(source);
        }

        if (Directory.Exists(dest))
        {
            Directory.Delete(dest, recursive: true);
        }

        Directory.CreateDirectory(dest);
        var psi = new ProcessStartInfo
        {
            FileName = "robocopy.exe",
            Arguments = $"\"{source}\" \"{dest}\" /E /NFL /NDL /NJH /NJS /nc /ns /np",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("robocopy failed to start");
        p.WaitForExit();
        if (p.ExitCode >= 8)
        {
            throw new InvalidOperationException($"复制失败: {source} -> {dest} (robocopy {p.ExitCode})");
        }
    }

    private static void CreateShortcut(string linkPath, string target, string workDir)
    {
        var dir = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell unavailable");
        var shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("WScript.Shell create failed");
        var shortcut = shellType.InvokeMember(
            "CreateShortcut",
            System.Reflection.BindingFlags.InvokeMethod,
            null,
            shell,
            [linkPath]);
        if (shortcut is null)
        {
            throw new InvalidOperationException("CreateShortcut failed");
        }

        var scType = shortcut.GetType();
        scType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, [target]);
        scType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, [workDir]);
        var ico = Path.Combine(workDir, "Assets", "AIExplorer.ico");
        if (File.Exists(ico))
        {
            scType.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcut, [ico]);
        }

        scType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, [DisplayName]);
        scType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunPowerShell(string script, bool ignoreExitCode)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"aiexplorer-setup-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(temp, script, Encoding.UTF8);
        try
        {
            var psExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                @"System32\WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(psExe)) psExe = "powershell.exe";

            var psi = new ProcessStartInfo
            {
                FileName = psExe,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{temp}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("powershell failed to start");
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (!ignoreExitCode && p.ExitCode != 0)
            {
                throw new InvalidOperationException($"PowerShell failed ({p.ExitCode}): {stderr}\n{stdout}");
            }

            return (p.ExitCode, stdout, stderr);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* ignore */ }
        }
    }

    private static string EscapePs(string path) => path.Replace("'", "''");

    private static string Trim(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 300 ? s.Substring(0, 300) + "..." : s;
    }
}
