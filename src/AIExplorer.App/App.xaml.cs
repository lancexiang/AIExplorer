using AIExplorer.Core.Extensions;
using AIExplorer.Core.Settings;
using AIExplorer.Infrastructure;
using AIExplorer_App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace AIExplorer_App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "Global\\AIExplorer.App.SingleInstance";
    private static Mutex? _singleInstanceMutex;
    private readonly IServiceProvider _services;

    public static Window Window { get; private set; } = null!;
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;
    public static IServiceProvider Services { get; private set; } = null!;
    public static List<Window> ActiveWindows { get; } = [];

    public static nint WindowHandle =>
        WindowNative.GetWindowHandle(Window);

    public static void TrackWindow(Window window)
    {
        if (!ActiveWindows.Contains(window))
        {
            ActiveWindows.Add(window);
            window.Closed += (_, _) => ActiveWindows.Remove(window);
        }
    }

    public App()
    {
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            NativeWindowHelper.ActivateExistingMainWindow();
            Environment.Exit(0);
            return;
        }

        InitializeComponent();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        _services = ConfigureServices();
        Services = _services;
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        CrashLog.Write("UI UnhandledException", e.Exception);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        CrashLog.Write("AppDomain UnhandledException", e.ExceptionObject as Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CrashLog.Write("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddAIExplorerInfrastructure();
        services.AddSingleton<ViewModels.FileColumnLayout>();
        services.AddTransient<MainPageViewModel>();
        services.AddTransient<SettingsViewModel>();
        return services.BuildServiceProvider();
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        await EnsureMainWindowAsync();
    }

    private async Task EnsureMainWindowAsync()
    {
        if (Window is not null)
        {
            Window.Activate();
            NativeWindowHelper.BringToForeground(WindowNative.GetWindowHandle(Window));
            return;
        }

        var settings = _services.GetRequiredService<ISettingsService>();
        await settings.LoadAsync();

        Window = new MainWindow();
        TrackWindow(Window);
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        SettingsViewModel.ApplyTheme(settings.Features.Theme);
        SettingsViewModel.ApplyAccent(settings.Features.AccentColor);
        SettingsViewModel.ApplyBackdrop(settings.Features.WindowBackdrop);
        Window.Activate();
    }
}
