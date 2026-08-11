using System.Text.Json;
using AIExplorer.Infrastructure.Terminal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;

namespace AIExplorer_App.Views;

public sealed partial class TerminalPane : UserControl, IDisposable
{
    private ConPtySession? _session;
    private bool _webReady;
    private bool _disposed;
    private string _cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly DispatcherQueue _dispatcher;
    private readonly object _outGate = new();
    private readonly List<byte> _pendingOut = [];
    private DispatcherQueueTimer? _flushTimer;
    private DispatcherQueueTimer? _fitTimer;
    private CoreWebView2? _core;
    private int _writingScript;
    private long _lastPasteRequestTicks;

    public event Action<string>? StatusChanged;

    public string WorkingDirectory => _cwd;

    public TerminalPane()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    public async Task EnsureStartedAsync(string? workingDirectory)
    {
        if (_disposed)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
        {
            _cwd = workingDirectory;
        }

        await EnsureWebViewAsync();
        if (_session is null || !_session.IsRunning)
        {
            StartSession(_cwd);
        }
        else
        {
            UpdateStatus();
        }

        await FocusTerminalAsync();
    }

    public void Restart(string? workingDirectory = null)
    {
        if (_disposed)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
        {
            _cwd = workingDirectory;
        }

        StartSession(_cwd);
        UpdateStatus();
        _ = FocusTerminalAsync();
    }

    public void ShutdownSession() => StopSession();

    public void ClearScreen()
    {
        if (_disposed || _core is null)
        {
            return;
        }

        _ = _core.ExecuteScriptAsync("termClear()");
    }

    public async Task FocusTerminalAsync()
    {
        if (_disposed || _core is null)
        {
            return;
        }

        try
        {
            await _core.ExecuteScriptAsync("termFocus()");
        }
        catch
        {
        }
    }

    private void UpdateStatus()
    {
        var hook = ConPtySession.ResolveCondaHookPath();
        var text = hook is null
            ? $"ConPTY · {_cwd}"
            : $"ConPTY · {_cwd} · conda_hook";
        StatusText.Text = text;
        StatusChanged?.Invoke(text);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureWebViewAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "WebView2 初始化失败: " + ex.Message;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _fitTimer ??= _dispatcher.CreateTimer();
        _fitTimer.Interval = TimeSpan.FromMilliseconds(80);
        _fitTimer.Tick -= OnFitTimerTick;
        _fitTimer.Tick += OnFitTimerTick;
        _fitTimer.Stop();
        _fitTimer.Start();
    }

    private void OnFitTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        _ = RequestFitAsync();
    }

    private async Task EnsureWebViewAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (TermView.CoreWebView2 is not null)
        {
            _core = TermView.CoreWebView2;
            return;
        }

        await TermView.EnsureCoreWebView2Async();
        if (_disposed)
        {
            return;
        }

        var core = TermView.CoreWebView2
                   ?? throw new InvalidOperationException("WebView2 CoreWebView2 is null");
        _core = core;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        // 避免浏览器层先吞掉 Ctrl+C/V，交给 xterm 自定义快捷键
        try
        {
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        }
        catch
        {
        }
        core.WebMessageReceived -= OnWebMessageReceived;
        core.WebMessageReceived += OnWebMessageReceived;

        var htmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal", "index.html");
        if (!File.Exists(htmlPath))
        {
            StatusText.Text = "缺少终端资源: Assets/Terminal/index.html";
            return;
        }

        core.Navigate(new Uri(htmlPath).AbsoluteUri);
    }

    private void StartSession(string cwd)
    {
        StopSession();
        try
        {
            _session = new ConPtySession();
            _session.OutputReceived += OnPtyOutput;
            _session.Exited += code => _dispatcher.TryEnqueue(() =>
            {
                if (!_disposed)
                {
                    StatusText.Text = $"终端已退出 ({code}) · 可用工具栏重启";
                }
            });
            _session.Start(cwd, cols: 120, rows: 28);
            UpdateStatus();
        }
        catch (Exception ex)
        {
            StatusText.Text = "ConPTY 启动失败: " + ex.Message;
            _session?.Dispose();
            _session = null;
        }
    }

    private void StopSession()
    {
        lock (_outGate)
        {
            _pendingOut.Clear();
        }

        if (_session is null)
        {
            return;
        }

        try
        {
            _session.OutputReceived -= OnPtyOutput;
        }
        catch
        {
        }

        _session.Dispose();
        _session = null;
    }

    private void OnPtyOutput(byte[] data)
    {
        if (data.Length == 0 || _disposed)
        {
            return;
        }

        lock (_outGate)
        {
            _pendingOut.AddRange(data);
            if (_pendingOut.Count > 512 * 1024)
            {
                _pendingOut.RemoveRange(0, _pendingOut.Count - 256 * 1024);
            }
        }

        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed)
            {
                return;
            }

            _flushTimer ??= _dispatcher.CreateTimer();
            _flushTimer.Interval = TimeSpan.FromMilliseconds(16);
            _flushTimer.Tick -= OnFlushTick;
            _flushTimer.Tick += OnFlushTick;
            if (!_flushTimer.IsRunning)
            {
                _flushTimer.Start();
            }
        });
    }

    private async void OnFlushTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_disposed || _core is null || !_webReady)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _writingScript, 1, 0) != 0)
        {
            sender.Start();
            return;
        }

        byte[] chunk;
        lock (_outGate)
        {
            if (_pendingOut.Count == 0)
            {
                Interlocked.Exchange(ref _writingScript, 0);
                return;
            }

            chunk = _pendingOut.ToArray();
            _pendingOut.Clear();
        }

        try
        {
            var b64 = Convert.ToBase64String(chunk);
            await _core.ExecuteScriptAsync($"termWriteBase64('{b64}')");
        }
        catch
        {
        }
        finally
        {
            Interlocked.Exchange(ref _writingScript, 0);
            lock (_outGate)
            {
                if (_pendingOut.Count > 0 && !_disposed)
                {
                    sender.Start();
                }
            }
        }
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var json = args.WebMessageAsJson;
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl))
            {
                return;
            }

            var type = typeEl.GetString();
            switch (type)
            {
                case "ready":
                    _webReady = true;
                    _ = RequestFitAsync();
                    break;
                case "input":
                    if (root.TryGetProperty("data", out var dataEl))
                    {
                        var text = dataEl.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            _session?.WriteText(text);
                        }
                    }

                    break;
                case "copy":
                    if (root.TryGetProperty("text", out var copyEl))
                    {
                        var copyText = copyEl.GetString();
                        if (!string.IsNullOrEmpty(copyText))
                        {
                            CopyTextToClipboard(copyText);
                        }
                    }

                    break;
                case "paste-request":
                    _ = PasteFromClipboardAsync();
                    break;
                case "resize":
                    if (root.TryGetProperty("cols", out var colsEl) &&
                        root.TryGetProperty("rows", out var rowsEl))
                    {
                        var cols = (short)Math.Clamp(colsEl.GetInt32(), 20, 400);
                        var rows = (short)Math.Clamp(rowsEl.GetInt32(), 8, 200);
                        _session?.Resize(cols, rows);
                    }

                    break;
            }
        }
        catch
        {
        }
    }

    private static void CopyTextToClipboard(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch
        {
        }
    }

    private async Task PasteFromClipboardAsync()
    {
        // 防抖：keydown + paste 事件可能连发
        var now = Environment.TickCount64;
        if (now - _lastPasteRequestTicks < 80)
        {
            return;
        }

        _lastPasteRequestTicks = now;

        if (_disposed || _session is null || !_session.IsRunning)
        {
            return;
        }

        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                return;
            }

            var text = await content.GetTextAsync();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            text = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                       .Replace('\n', '\r');
            _session.WriteText(text);
        }
        catch
        {
        }
    }

    private async Task RequestFitAsync()
    {
        if (_disposed || _core is null || !_webReady)
        {
            return;
        }

        try
        {
            await _core.ExecuteScriptAsync(
                "try{fitAddon.fit();chrome.webview.postMessage({type:'resize',cols:term.cols,rows:term.rows})}catch(e){}");
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try { _flushTimer?.Stop(); } catch { }
        try { _fitTimer?.Stop(); } catch { }

        if (_core is not null)
        {
            try
            {
                _core.WebMessageReceived -= OnWebMessageReceived;
            }
            catch
            {
            }

            _core = null;
        }

        _webReady = false;
        StopSession();
    }
}
