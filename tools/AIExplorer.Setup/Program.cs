namespace AIExplorer.Setup;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var silent = HasFlag(args, "/silent") || HasFlag(args, "--silent") || HasFlag(args, "/S");
        var uninstall = HasFlag(args, "/uninstall") || HasFlag(args, "--uninstall") ||
                        IsUninstallExeName();

        try
        {
            if (uninstall)
            {
                if (silent)
                {
                    new InstallEngine().Uninstall();
                    return;
                }

                Application.Run(new UninstallForm());
                return;
            }

            var packageRoot = InstallEngine.FindPackageRoot();

            if (silent)
            {
                var engine = new InstallEngine();
                var dest = InstallEngine.DefaultInstallDir;
                engine.Install(packageRoot, dest, createDesktopShortcut: true, createStartMenuShortcut: true, (_, _) => { });
                InstallEngine.LaunchApp(dest);
                return;
            }

            Application.Run(new WizardForm(packageRoot));
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                MessageBox.Show(
                    ex.Message,
                    uninstall ? "卸载失败" : "AIExplorer Setup 失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            Environment.ExitCode = 1;
        }
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static bool IsUninstallExeName()
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            return string.Equals(name, "Uninstall", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
