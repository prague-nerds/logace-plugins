using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace LogAce.Plugins.Gcp;

/// <summary>Owns a reconnecting CLI producer and its live spool.</summary>
internal sealed class CliLineFollowSession : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly string _displayPath;
    private readonly string _spoolPath;
    private readonly Func<int, string?, Process> _startAttempt;
    private readonly Func<string, string> _buildError;
    private readonly IStreamingOutputTransformer _transformer;
    private readonly Func<string, string?>? _extractTimestampRaw;
    private readonly FileStream _output;
    private readonly StreamWriter _writer;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly TaskCompletionSource<string?> _startupResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _workerTask;
    private readonly object _processSync = new();
    private readonly TimestampReplayCursor _cursor;
    private Process? _currentProcess;
    private bool _disposed;

    private CliLineFollowSession(
        string displayPath,
        string spoolPath,
        Func<int, string?, Process> startAttempt,
        Func<string, string> buildError,
        IStreamingOutputTransformer transformer,
        Func<string, string?>? extractTimestampRaw,
        FileStream output,
        CancellationTokenSource cancellationTokenSource)
    {
        _displayPath = displayPath;
        _spoolPath = spoolPath;
        _startAttempt = startAttempt;
        _buildError = buildError;
        _transformer = transformer;
        _extractTimestampRaw = extractTimestampRaw;
        _output = output;
        _writer = new StreamWriter(output, new UTF8Encoding(false), 16 * 1024, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        _cancellationTokenSource = cancellationTokenSource;
        _cursor = new TimestampReplayCursor(extractTimestampRaw);
        _workerTask = Task.Run(RunFollowLoop);
    }

    public static bool TryStart(
        string displayPath,
        string spoolPath,
        Func<int, string?, Process> startAttempt,
        Func<string, string> buildError,
        IStreamingOutputTransformer transformer,
        Func<string, string?>? extractTimestampRaw,
        out CliLineFollowSession? session,
        out string? error)
    {
        session = null;
        error = null;
        FileStream? output = null;
        CliLineFollowSession? createdSession = null;

        try
        {
            output = new FileStream(
                spoolPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);

            CancellationTokenSource cancellationTokenSource = new();
            createdSession = new CliLineFollowSession(
                displayPath,
                spoolPath,
                startAttempt,
                buildError,
                transformer,
                extractTimestampRaw,
                output,
                cancellationTokenSource);

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
            CloudCliProcess.TryDeleteFile(spoolPath);
            error = $"Could not start follow session for '{displayPath}': {ex.Message}";
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
        CloudCliProcess.TryDeleteFile(_spoolPath);
    }

    private void RunFollowLoop()
    {
        bool startupCompleted = false;
        int attempt = 0;

        try
        {
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                string? sinceTimestamp = _cursor.BeginAttempt();
                Process? process = null;
                Task<string>? stderrTask = null;

                try
                {
                    process = _startAttempt(attempt++, sinceTimestamp);
                    stderrTask = SourceRuntime.ReadDiagnosticsAsync(process.StandardError);
                    SetCurrentProcess(process);

                    bool exitedQuickly = process.WaitForExit(250);

                    if (!startupCompleted)
                    {
                        if (exitedQuickly && process.ExitCode != 0)
                        {
                            _startupResult.TrySetResult(_buildError(SafeGetTaskResult(stderrTask)));
                            return;
                        }

                        startupCompleted = true;
                        _startupResult.TrySetResult(null);
                    }

                    PumpOutput(process.StandardOutput);

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
                        _startupResult.TrySetResult($"Could not start follow session for '{_displayPath}': {ex.Message}");
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
                _startupResult.TrySetResult($"Follow session for '{_displayPath}' exited before it could start.");
            }
        }
    }

    private void PumpOutput(StreamReader reader)
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

            foreach (string outputLine in _transformer.ProcessLine(line))
            {
                WriteOutputLine(outputLine);
            }
        }

        foreach (string outputLine in _transformer.Complete())
        {
            WriteOutputLine(outputLine);
        }
    }

    private void WriteOutputLine(string line)
    {
        if (_cursor.ShouldSkipReplay(line))
        {
            return;
        }

        _writer.WriteLine(line);
        _cursor.RecordWrittenLine(line);
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

    private sealed class TimestampReplayCursor
    {
        private readonly Func<string, string?>? _extractTimestampRaw;
        private readonly string _reconnectBaseline = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        private readonly Dictionary<string, int> _linesAtLastTimestamp = new(StringComparer.Ordinal);
        private bool _useInitialAttempt = true;
        private string? _lastTimestampRaw;
        private string? _pendingReplayTimestampRaw;
        private Dictionary<string, int>? _pendingReplayLineCounts;

        public TimestampReplayCursor(Func<string, string?>? extractTimestampRaw)
        {
            _extractTimestampRaw = extractTimestampRaw;
        }

        public string? BeginAttempt()
        {
            if (_extractTimestampRaw is null)
            {
                return null;
            }

            if (_useInitialAttempt)
            {
                _useInitialAttempt = false;
                ClearPendingReplay();
                return null;
            }

            _pendingReplayTimestampRaw = _lastTimestampRaw;
            _pendingReplayLineCounts = _linesAtLastTimestamp.Count == 0
                ? null
                : _linesAtLastTimestamp.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

            return _lastTimestampRaw ?? _reconnectBaseline;
        }

        public bool ShouldSkipReplay(string line)
        {
            if (_extractTimestampRaw is null
                || _pendingReplayTimestampRaw is null
                || _pendingReplayLineCounts is null)
            {
                return false;
            }

            string? timestampRaw = _extractTimestampRaw(line);

            if (timestampRaw is null)
            {
                return false;
            }

            int comparison = string.CompareOrdinal(timestampRaw, _pendingReplayTimestampRaw);

            if (comparison > 0)
            {
                ClearPendingReplay();
                return false;
            }

            if (comparison < 0)
            {
                return true;
            }

            if (_pendingReplayLineCounts.TryGetValue(line, out int remaining) && remaining > 0)
            {
                if (remaining == 1)
                {
                    _pendingReplayLineCounts.Remove(line);
                }
                else
                {
                    _pendingReplayLineCounts[line] = remaining - 1;
                }

                if (_pendingReplayLineCounts.Count == 0)
                {
                    ClearPendingReplay();
                }

                return true;
            }

            return false;
        }

        public void RecordWrittenLine(string line)
        {
            if (_extractTimestampRaw is null)
            {
                return;
            }

            string? timestampRaw = _extractTimestampRaw(line);

            if (timestampRaw is null)
            {
                return;
            }

            if (_lastTimestampRaw is null)
            {
                _lastTimestampRaw = timestampRaw;
                _linesAtLastTimestamp.Clear();
                _linesAtLastTimestamp[line] = 1;
                return;
            }

            int comparison = string.CompareOrdinal(timestampRaw, _lastTimestampRaw);

            if (comparison > 0)
            {
                _lastTimestampRaw = timestampRaw;
                _linesAtLastTimestamp.Clear();
                _linesAtLastTimestamp[line] = 1;
                return;
            }

            if (comparison == 0)
            {
                _linesAtLastTimestamp[line] = _linesAtLastTimestamp.GetValueOrDefault(line) + 1;
            }
        }

        private void ClearPendingReplay()
        {
            _pendingReplayTimestampRaw = null;
            _pendingReplayLineCounts = null;
        }
    }
}
