using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text;

namespace LogAce.Plugins.Logcat;

/// <summary>Owns live logcat, replay cutoff and package PID re-resolution.</summary>
internal sealed class LogcatFollowSession : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly LogcatSourceOptions _options;
    private readonly FileStream _output;
    private readonly StreamWriter _writer;
    private readonly Task _workerTask;
    private readonly TaskCompletionSource<string?> _startupResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly string _spoolPath;
    private readonly object _processSync = new();
    private Process? _currentProcess;
    private string? _lastTimestamp;
    private bool _disposed;

    private LogcatFollowSession(
        LogcatSourceOptions options,
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
        LogcatSourceOptions options,
        string spoolPath,
        out LogcatFollowSession? session,
        out string? error)
    {
        session = null;
        error = null;

        FileStream? output = null;
        LogcatFollowSession? createdSession = null;

        try
        {
            output = new FileStream(
                spoolPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);

            if (options.ClearBufferFirst)
            {
                TryClearBuffer(options);
            }

            CancellationTokenSource cancellationTokenSource = new();
            createdSession = new LogcatFollowSession(options, output, cancellationTokenSource, spoolPath);
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
            error = $"Could not start Android logcat follow session: {ex.Message}";
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
                string? cursor = _lastTimestamp;
                bool isReconnect = cursor is not null;
                LogcatSourceOptions attemptOptions = ResolveAttemptOptions();
                Process? process = null;
                Task<string>? stderrTask = null;

                try
                {
                    process = AdbDeviceCatalog.StartAdbProcess(
                        LogcatSourceFactory.BuildLogcatArguments(attemptOptions, follow: true, sinceTimestamp: cursor));
                    stderrTask = SourceRuntime.ReadDiagnosticsAsync(process.StandardError);
                    SetCurrentProcess(process);

                    bool exitedQuickly = process.WaitForExit(300);

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

                    PumpFollowOutput(process.StandardOutput, isReconnect ? cursor : null);

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
                        _startupResult.TrySetResult($"Could not start Android logcat follow session: {ex.Message}");
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
                _startupResult.TrySetResult("Android logcat follow session exited before it could start.");
            }
        }
    }

    private LogcatSourceOptions ResolveAttemptOptions()
    {
        if (string.IsNullOrWhiteSpace(_options.Package) || _options.DeviceSerial is null)
        {
            return _options;
        }

        // The app may have restarted with a new PID between reconnects; re-resolve it.
        if (AdbPackageCatalog.TryResolvePid(_options.DeviceSerial, _options.Package!, out int? resolvedPid, out _)
            && resolvedPid is not null)
        {
            return _options with
            {
                ProcessId = resolvedPid
            };
        }

        return _options;
    }

    private void PumpFollowOutput(StreamReader reader, string? replayCutoff)
    {
        bool skippingReplay = replayCutoff is not null;

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

            string? timestamp = LogcatSourceFactory.TryExtractTimestamp(line);

            if (skippingReplay && timestamp is not null)
            {
                if (string.CompareOrdinal(timestamp, replayCutoff) <= 0)
                {
                    continue;
                }

                skippingReplay = false;
            }

            _writer.WriteLine(line);

            if (timestamp is not null)
            {
                _lastTimestamp = timestamp;
            }
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

    private static void TryClearBuffer(LogcatSourceOptions options)
    {
        try
        {
            var arguments = new List<string>();

            if (!string.IsNullOrWhiteSpace(options.DeviceSerial))
            {
                arguments.Add("-s");
                arguments.Add(options.DeviceSerial!);
            }

            arguments.Add("logcat");
            arguments.Add("-c");

            using Process process = AdbDeviceCatalog.StartAdbProcess(arguments);
            _ = SourceRuntime.ReadResult(process);
        }
        catch
        {
        }
    }

    private static string BuildStartupError(string stderr)
    {
        return string.IsNullOrWhiteSpace(stderr)
            ? "Could not start Android logcat follow session."
            : $"Could not start Android logcat follow session: {stderr}";
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
