using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text;

namespace LogAce.Plugins.OsLog;

/// <summary>Owns a reconnecting macOS unified-log stream.</summary>
internal sealed class OsLogFollowSession : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly OsLogSourceOptions _options;
    private readonly FileStream _output;
    private readonly StreamWriter _writer;
    private readonly Task _workerTask;
    private readonly TaskCompletionSource<string?> _startupResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly string _spoolPath;
    private readonly object _processSync = new();
    private Process? _currentProcess;
    private bool _disposed;

    private OsLogFollowSession(
        OsLogSourceOptions options,
        FileStream output,
        CancellationTokenSource cancellationTokenSource,
        string spoolPath)
    {
        _options = options;
        _output = output;
        _writer = new StreamWriter(output, new UTF8Encoding(false), 16 * 1024, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        _cancellationTokenSource = cancellationTokenSource;
        _spoolPath = spoolPath;
        _workerTask = Task.Run(RunFollowLoop);
    }

    public static bool TryStart(
        OsLogSourceOptions options,
        string spoolPath,
        out OsLogFollowSession? session,
        out string? error)
    {
        session = null;
        error = null;

        FileStream? output = null;
        OsLogFollowSession? createdSession = null;

        try
        {
            output = new FileStream(
                spoolPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);

            CancellationTokenSource cancellationTokenSource = new();
            createdSession = new OsLogFollowSession(options, output, cancellationTokenSource, spoolPath);
            string? startupError = createdSession._startupResult.Task.GetAwaiter().GetResult();

            if (startupError is not null)
            {
                createdSession.Dispose();
                error = startupError;
                return false;
            }

            session = createdSession;
            return true;
        }
        catch (Exception ex)
        {
            createdSession?.Dispose();
            output?.Dispose();
            TryDeleteFile(spoolPath);
            error = $"Could not start macOS unified log follow session: {ex.Message}";
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _cancellationTokenSource.Cancel();
        }
        catch
        {
        }

        try
        {
            Process? processToStop;

            lock (_processSync)
            {
                processToStop = _currentProcess;
            }

            if (processToStop is not null && !processToStop.HasExited)
            {
                processToStop.Kill(entireProcessTree: true);
                processToStop.WaitForExit(500);
            }
        }
        catch
        {
        }

        try
        {
            _workerTask.Wait(2000);
        }
        catch
        {
        }

        _writer.Dispose();
        _output.Dispose();
        _cancellationTokenSource.Dispose();
        TryDeleteFile(_spoolPath);
    }

    private void RunFollowLoop()
    {
        bool startupCompleted = false;

        try
        {
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                Process? process = null;
                Task<string>? stderrTask = null;

                try
                {
                    process = OsLogSourceFactory.StartLogProcess(_options);
                    stderrTask = SourceRuntime.ReadDiagnosticsAsync(process.StandardError);
                    SetCurrentProcess(process);

                    bool exitedQuickly = process.WaitForExit(250);

                    if (!startupCompleted)
                    {
                        if (exitedQuickly && process.ExitCode != 0)
                        {
                            _startupResult.TrySetResult(BuildStartupError(SafeGetTaskResult(stderrTask)));
                            return;
                        }

                        startupCompleted = true;
                        _startupResult.TrySetResult(null);
                    }

                    PumpFollowOutput(process.StandardOutput);

                    try
                    {
                        process.WaitForExit();
                    }
                    catch
                    {
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!startupCompleted)
                    {
                        _startupResult.TrySetResult($"Could not start macOS unified log follow session: {ex.Message}");
                        return;
                    }
                }
                finally
                {
                    ClearCurrentProcess(process);

                    try
                    {
                        process?.Dispose();
                    }
                    catch
                    {
                    }

                    ObserveTask(stderrTask);
                }

                if (_cancellationTokenSource.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    Task.Delay(RetryDelay, _cancellationTokenSource.Token).Wait(_cancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            if (!startupCompleted)
            {
                _startupResult.TrySetResult("macOS unified log follow session exited before it could start.");
            }
        }
    }

    private void PumpFollowOutput(StreamReader reader)
    {
        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            string? line;

            try
            {
                line = reader.ReadLine();
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            if (!OsLogSourceFactory.IsLogEventLine(line))
            {
                continue;
            }

            _writer.WriteLine(line);
        }
    }

    private void SetCurrentProcess(Process process)
    {
        lock (_processSync)
        {
            _currentProcess = process;
        }
    }

    private void ClearCurrentProcess(Process? process)
    {
        lock (_processSync)
        {
            if (ReferenceEquals(_currentProcess, process))
            {
                _currentProcess = null;
            }
        }
    }

    private static string BuildStartupError(string stderr)
    {
        return string.IsNullOrWhiteSpace(stderr)
            ? "Could not start macOS unified log follow session."
            : $"Could not start macOS unified log follow session: {stderr}";
    }

    private static string SafeGetTaskResult(Task<string>? task)
    {
        if (task is null)
        {
            return string.Empty;
        }

        try
        {
            return task.GetAwaiter().GetResult().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void ObserveTask(Task<string>? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            _ = task.IsCompleted;
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
