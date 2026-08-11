using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using static AIExplorer.Infrastructure.Terminal.ConPtyNative;

namespace AIExplorer.Infrastructure.Terminal;

/// <summary>
/// Windows ConPTY 会话。注意：用户 CMD AutoRun 里的 ansicon 等注入工具
/// 在伪控制台下常触发 0xc0000142，因此默认 cmd /d，再手动挂 conda_hook。
/// </summary>
public sealed class ConPtySession : IDisposable
{
    private readonly object _gate = new();
    private IntPtr _pseudoConsole = IntPtr.Zero;
    private IntPtr _process = IntPtr.Zero;
    private IntPtr _thread = IntPtr.Zero;
    private SafeFileHandle? _inputWrite;
    private SafeFileHandle? _outputRead;
    private FileStream? _inputStream;
    private FileStream? _outputStream;
    private CancellationTokenSource? _readCts;
    private bool _disposed;

    public event Action<byte[]>? OutputReceived;
    public event Action<uint>? Exited;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _process != IntPtr.Zero && !_disposed;
            }
        }
    }

    public void Start(string workingDirectory, short cols = 120, short rows = 30, string? shellCommand = null)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ConPtySession));
            }

            if (_process != IntPtr.Zero)
            {
                return;
            }

            cols = Math.Max(cols, (short)20);
            rows = Math.Max(rows, (short)8);

            var cwd = string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : workingDirectory;

            CreatePipePair(out var inputRead, out _inputWrite);
            CreatePipePair(out _outputRead, out var outputWrite);

            var size = new COORD { X = cols, Y = rows };
            var hr = CreatePseudoConsole(size, inputRead, outputWrite, 0, out _pseudoConsole);
            inputRead.Dispose();
            outputWrite.Dispose();
            if (hr != 0)
            {
                throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X8}");
            }

            var command = string.IsNullOrWhiteSpace(shellCommand)
                ? BuildCmdCommandLine()
                : shellCommand;

            StartProcessAttachedToPseudoConsole(command, cwd);

            _inputStream = new FileStream(_inputWrite!, FileAccess.Write, 4096, isAsync: false);
            _outputStream = new FileStream(_outputRead!, FileAccess.Read, 4096, isAsync: false);
            _readCts = new CancellationTokenSource();
            _ = Task.Run(() => ReadLoopAsync(_readCts.Token));
            _ = Task.Run(WaitForExitLoop);
        }
    }

    /// <summary>
    /// /d 禁用 AutoRun（跳过 ansicon 注入，避免 ConPTY 下 0xc0000142），
    /// 再显式 call conda_hook.bat，保证 mamba/conda activate 可用。
    /// </summary>
    public static string BuildCmdCommandLine()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var hook = ResolveCondaHookPath();
        if (hook is not null)
        {
            return $"\"{cmd}\" /d /k call \"{hook}\"";
        }

        return $"\"{cmd}\" /d";
    }

    public static string? ResolveCondaHookPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Command Processor");
            var autoRun = key?.GetValue("AutoRun") as string;
            if (!string.IsNullOrWhiteSpace(autoRun))
            {
                // ... if exist "D:\...\conda_hook.bat" "D:\...\conda_hook.bat"
                var marker = "conda_hook.bat";
                var idx = autoRun.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var start = autoRun.LastIndexOf('"', idx);
                    var end = autoRun.IndexOf('"', idx);
                    if (start >= 0 && end > start)
                    {
                        var path = autoRun.Substring(start + 1, end - start - 1);
                        if (File.Exists(path))
                        {
                            return path;
                        }
                    }
                }
            }
        }
        catch
        {
        }

        foreach (var candidate in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"anaconda3\condabin\conda_hook.bat"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"miniconda3\condabin\conda_hook.bat"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"mambaforge\condabin\conda_hook.bat"),
                     @"D:\ProgramData\anaconda3\condabin\conda_hook.bat",
                     @"C:\ProgramData\anaconda3\condabin\conda_hook.bat",
                     @"C:\ProgramData\mambaforge\condabin\conda_hook.bat",
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_inputStream is null || _disposed)
            {
                return;
            }

            _inputStream.Write(data);
            _inputStream.Flush();
        }
    }

    public void WriteText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        Write(Encoding.UTF8.GetBytes(text));
    }

    public void Resize(short cols, short rows)
    {
        lock (_gate)
        {
            if (_pseudoConsole == IntPtr.Zero || _disposed)
            {
                return;
            }

            ResizePseudoConsole(_pseudoConsole, new COORD
            {
                X = Math.Max(cols, (short)20),
                Y = Math.Max(rows, (short)8),
            });
        }
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        var buffer = new byte[4096];
        try
        {
            while (!token.IsCancellationRequested)
            {
                FileStream? stream;
                lock (_gate)
                {
                    stream = _outputStream;
                }

                if (stream is null)
                {
                    break;
                }

                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                var copy = new byte[read];
                Buffer.BlockCopy(buffer, 0, copy, 0, read);
                OutputReceived?.Invoke(copy);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private void WaitForExitLoop()
    {
        IntPtr process;
        lock (_gate)
        {
            process = _process;
        }

        if (process == IntPtr.Zero)
        {
            return;
        }

        WaitForSingleObject(process, 0xFFFFFFFF);
        GetExitCodeProcess(process, out var code);
        Exited?.Invoke(code);
    }

    private void StartProcessAttachedToPseudoConsole(string commandLine, string workingDirectory)
    {
        var lpSize = IntPtr.Zero;
        var ok = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
        if (ok || lpSize == IntPtr.Zero)
        {
            throw new InvalidOperationException("InitializeProcThreadAttributeList(size) failed: " + Marshal.GetLastWin32Error());
        }

        var startupInfo = new STARTUPINFOEX();
        // MiniTerm / MSDN：cb = sizeof(STARTUPINFOEX)
        startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        startupInfo.StartupInfo.dwFlags = STARTF_USESHOWWINDOW;
        startupInfo.StartupInfo.wShowWindow = SW_HIDE;
        startupInfo.lpAttributeList = Marshal.AllocHGlobal(lpSize);
        try
        {
            ok = InitializeProcThreadAttributeList(startupInfo.lpAttributeList, 1, 0, ref lpSize);
            if (!ok)
            {
                throw new InvalidOperationException("InitializeProcThreadAttributeList failed: " + Marshal.GetLastWin32Error());
            }

            // 与 MiniTerm 一致：lpValue 直接传 HPCON 句柄值（不是 &HPCON）
            ok = UpdateProcThreadAttribute(
                startupInfo.lpAttributeList,
                0,
                (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                _pseudoConsole,
                (IntPtr)IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero);
            if (!ok)
            {
                throw new InvalidOperationException("UpdateProcThreadAttribute failed: " + Marshal.GetLastWin32Error());
            }

            var pSec = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>() };
            var tSec = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>() };
            var cmd = new StringBuilder(commandLine);

            ok = CreateProcessW(
                null,
                cmd,
                ref pSec,
                ref tSec,
                false,
                EXTENDED_STARTUPINFO_PRESENT,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out var processInfo);
            if (!ok)
            {
                throw new InvalidOperationException("CreateProcessW failed: " + Marshal.GetLastWin32Error());
            }

            _process = processInfo.hProcess;
            _thread = processInfo.hThread;
        }
        finally
        {
            if (startupInfo.lpAttributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(startupInfo.lpAttributeList);
                Marshal.FreeHGlobal(startupInfo.lpAttributeList);
            }
        }
    }

    private static void CreatePipePair(out SafeFileHandle read, out SafeFileHandle write)
    {
        // MiniTerm：lpPipeAttributes = NULL
        if (!CreatePipe(out read, out write, IntPtr.Zero, 0))
        {
            throw new InvalidOperationException("CreatePipe failed: " + Marshal.GetLastWin32Error());
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        try { _readCts?.Cancel(); } catch { }

        try { _inputStream?.Dispose(); } catch { }
        try { _outputStream?.Dispose(); } catch { }
        try { _inputWrite?.Dispose(); } catch { }
        try { _outputRead?.Dispose(); } catch { }

        if (_pseudoConsole != IntPtr.Zero)
        {
            ClosePseudoConsole(_pseudoConsole);
            _pseudoConsole = IntPtr.Zero;
        }

        if (_thread != IntPtr.Zero)
        {
            CloseHandle(_thread);
            _thread = IntPtr.Zero;
        }

        if (_process != IntPtr.Zero)
        {
            // 先关伪控制台再关进程，避免挂起
            try
            {
                // already closed above
            }
            catch
            {
            }

            CloseHandle(_process);
            _process = IntPtr.Zero;
        }

        _readCts?.Dispose();
    }
}
