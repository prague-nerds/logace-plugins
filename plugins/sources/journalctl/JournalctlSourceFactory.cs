using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text;
using System.Buffers;
using LogAce.PluginApi;

namespace LogAce.Plugins.Journalctl;

/// <summary>Creates provider captures and canonical identities; the public plugin owns validation and operation scope.</summary>
internal static class JournalctlSourceFactory
{
    public static string GetDisplayPath(JournalctlSourceOptions options)
    {
        return BuildDisplayPath(options);
    }

    public static bool TryCreateCore(
        JournalctlSourceOptions options,
        out LogSourceHandle? source,
        out string? error)
    {
        source = null;
        error = null;

        string spoolPath = BuildSpoolPath(options);
        string displayPath = BuildDisplayPath(options);

        string shortName = string.IsNullOrWhiteSpace(options.Unit) ? "journalctl" : options.Unit.Trim();

        try
        {
            if (options.Follow)
            {
                if (!JournalctlFollowSession.TryStart(options, spoolPath, out JournalctlFollowSession? session, out error))
                {
                    return false;
                }

                source = new LogSourceHandle(
                    spoolPath,
                    displayPath,
                    shortName,

                    preferredProfileId: "journalctl",
                    supportsRecentHistory: false,
                    supportsDuplicate: false,
                    autoReloadOnExternalChange: true,
                    isFileSystemSource: false,
                    lifetime: session);
                return true;
            }

            if (!TryCaptureSnapshot(options, spoolPath, out error))
            {
                TryDeleteFile(spoolPath);
                return false;
            }

            source = new LogSourceHandle(
                spoolPath,
                displayPath,
                shortName,

                preferredProfileId: "journalctl",
                supportsRecentHistory: false,
                supportsDuplicate: false,
                autoReloadOnExternalChange: false,
                isFileSystemSource: false,
                lifetime: new TemporaryFileLifetime(spoolPath));
            return true;
        }
        catch (Exception ex)
        {
            TryDeleteFile(spoolPath);
            error = $"Could not open journalctl logs for '{displayPath}': {ex.Message}";
            return false;
        }
    }

    private static bool TryCaptureSnapshot(
        JournalctlSourceOptions options,
        string spoolPath,
        out string? error)
    {
        error = null;

        using Process process = StartJournalctlProcess(options, follow: false);
        Task<string> stderrTask = SourceRuntime.ReadDiagnosticsAsync(process.StandardError);

        using (var output = new FileStream(
                   spoolPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            CopyOutput(process.StandardOutput.BaseStream, output, SourceRuntime.OpeningToken);
        }

        process.WaitForExit();
        string stderr = stderrTask.GetAwaiter().GetResult().Trim();

        if (process.ExitCode == 0)
        {
            return true;
        }

        error = BuildJournalctlError(options, stderr);
        return false;
    }

    private static string BuildSpoolPath(JournalctlSourceOptions options) => SourceRuntime.CreateSpool(".log");

    private static string BuildDisplayPath(JournalctlSourceOptions options)
    {
        return string.IsNullOrWhiteSpace(options.Unit)
            ? "journalctl://all"
            : $"journalctl://{options.Unit}";
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(
            value
                .Trim()
                .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
                .ToArray());

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "journalctl-log";
        }

        return sanitized.Length <= 64 ? sanitized : sanitized[..64];
    }

    private static Process StartJournalctlProcess(JournalctlSourceOptions options, bool follow)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "journalctl",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("--no-pager");
        startInfo.ArgumentList.Add("--output=short-iso-precise");

        string tail = string.IsNullOrWhiteSpace(options.Tail)
            ? "all"
            : options.Tail.Trim();

        if (tail.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            if (follow)
            {
                startInfo.ArgumentList.Add("--no-tail");
            }
        }
        else
        {
            startInfo.ArgumentList.Add("--lines");
            startInfo.ArgumentList.Add(tail);
        }

        JournalctlSinceArgumentBuilder.Append(startInfo.ArgumentList, options.Since);

        if (!string.IsNullOrWhiteSpace(options.Unit))
        {
            startInfo.ArgumentList.Add("--unit");
            startInfo.ArgumentList.Add(options.Unit);
        }

        if (follow)
        {
            startInfo.ArgumentList.Add("--follow");
        }

        try
        {
            Process? process = SourceRuntime.Start(startInfo);
            return process ?? throw new InvalidOperationException("journalctl process did not start.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not start journalctl. Ensure 'journalctl' is installed and available on PATH. {ex.Message}",
                ex);
        }
    }

    private static string BuildJournalctlError(JournalctlSourceOptions options, string stderr)
    {
        string displayPath = BuildDisplayPath(options);
        return string.IsNullOrWhiteSpace(stderr)
            ? $"journalctl command failed for '{displayPath}'."
            : $"journalctl command failed for '{displayPath}': {stderr}";
    }

    private static void CopyOutput(Stream input, Stream output, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);

        try
        {
            while (true)
            {
                int read = input.Read(buffer, 0, buffer.Length);

                if (read == 0)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                output.Flush();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
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

    private sealed class JournalctlFollowSession : IDisposable
    {
        private readonly FileStream _output;
        private readonly StreamWriter _writer;
        private readonly Task _workerTask;
        private readonly TaskCompletionSource<string?> _startupResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly string _spoolPath;
        private readonly object _processSync = new();
        private readonly JournalctlSourceOptions _options;
        private Process? _currentProcess;
        private bool _disposed;

        private JournalctlFollowSession(
            JournalctlSourceOptions options,
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
            JournalctlSourceOptions options,
            string spoolPath,
            out JournalctlFollowSession? session,
            out string? error)
        {
            session = null;
            error = null;

            FileStream? output = null;
            JournalctlFollowSession? createdSession = null;

            try
            {
                output = new FileStream(
                    spoolPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);

                CancellationTokenSource cancellationTokenSource = new();
                createdSession = new JournalctlFollowSession(options, output, cancellationTokenSource, spoolPath);
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
                error = $"Could not start journalctl follow session for '{BuildDisplayPath(options)}': {ex.Message}";
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
            Process? process = null;
            Task<string>? stderrTask = null;

            try
            {
                process = StartJournalctlProcess(_options, follow: true);
                stderrTask = SourceRuntime.ReadDiagnosticsAsync(process.StandardError);
                SetCurrentProcess(process);

                bool exitedQuickly = process.WaitForExit(250);

                if (exitedQuickly && process.ExitCode != 0)
                {
                    _startupResult.TrySetResult(BuildJournalctlError(_options, SafeGetTaskResult(stderrTask)));
                    return;
                }

                startupCompleted = true;
                _startupResult.TrySetResult(null);
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
            }
            catch (Exception ex)
            {
                _startupResult.TrySetResult(
                    $"Could not start journalctl follow session for '{BuildDisplayPath(_options)}': {ex.Message}");
            }
            finally
            {
                if (!startupCompleted)
                {
                    _startupResult.TrySetResult("journalctl follow session exited before it could start.");
                }

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
    }
}
