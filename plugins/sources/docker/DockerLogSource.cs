using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LogAce.PluginApi;

namespace LogAce.Plugins.Docker;

internal static class DockerLogSource
{
    internal static async Task<ILogSource> OpenAsync(DockerOptions options, PluginSourceOpenContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        string path = context.Host.CreateSpoolPath("docker.source");
        IDisposable? lifetime = null;
        try
        {
            lifetime = options.Follow ? await DockerFollowSession.StartAsync(options, path, context.CancellationToken).ConfigureAwait(false)
                : await CaptureAsync(options, path, context.CancellationToken).ConfigureAwait(false);
            context.CancellationToken.ThrowIfCancellationRequested();
            var source = new LogSourceHandle(path, options.DisplayPath, options.Container, "docker",
                supportsRecentHistory: true, supportsDuplicate: false, autoReloadOnExternalChange: options.Follow,
                isFileSystemSource: false, lifetime: lifetime);
            lifetime = null; // Ownership transfers to the returned handle only on success.
            return source;
        }
        catch { lifetime?.Dispose(); Delete(path); throw; }
    }

    private static async Task<IDisposable> CaptureAsync(DockerOptions options, string path, CancellationToken token)
    {
        using Process process = DockerCli.StartLogs(options);
        using var cancellation = token.Register(() => DockerCli.Kill(process));
        using var output = OpenSpool(path);
        using var writer = new StreamWriter(output, new UTF8Encoding(false), 16384) { NewLine = "\n" };
        var sink = new LogSink(writer, null);
        await DrainAsync(process, sink, token).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new IOException(sink.Error("Could not read Docker logs."));
        token.ThrowIfCancellationRequested();
        return new SpoolLifetime(path);
    }

    internal static FileStream OpenSpool(string path) => new(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    internal static async Task DrainAsync(Process process, LogSink sink, CancellationToken token)
    {
        // A failed spool write must also stop the other pipe and the process. Waiting
        // for WhenAll alone could otherwise hang on a still-running live producer.
        async Task Pump(StreamReader reader, bool stderr)
        {
            try { await sink.PumpAsync(reader, stderr, token).ConfigureAwait(false); }
            catch { DockerCli.Kill(process); throw; }
        }
        await Task.WhenAll(Pump(process.StandardOutput, false), Pump(process.StandardError, true),
            process.WaitForExitAsync(token)).ConfigureAwait(false);
    }

    internal static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class SpoolLifetime(string path) : IDisposable
    {
        public void Dispose() => Delete(path);
    }
}

/// <summary>
/// Docker writes container stdout and stderr to separate CLI pipes. Drain both
/// concurrently to prevent deadlock; serialize only the shared spool/cursor update.
/// Timestamped stderr is log data, while unprefixed stderr is a CLI diagnostic.
/// </summary>
internal sealed class LogSink(StreamWriter writer, DockerFollowCursor? cursor)
{
    private readonly object _sync = new();
    private readonly StringBuilder _diagnostics = new();

    internal async Task PumpAsync(StreamReader reader, bool stderr, CancellationToken token)
    {
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            lock (_sync)
            {
                if (stderr && DockerFollowCursor.Timestamp(line) is null)
                {
                    if (_diagnostics.Length < 8192) _diagnostics.AppendLine(line[..Math.Min(line.Length, 8192 - _diagnostics.Length)]);
                    continue;
                }
                if (cursor?.ShouldSkipReplay(line) == true) continue;
                writer.WriteLine(line);
                cursor?.Record(line);
            }
        }
    }

    internal string Error(string fallback)
    {
        lock (_sync) return _diagnostics.Length == 0 ? fallback : _diagnostics.ToString().Trim();
    }
}

/// <summary>A source-owned background worker, released when the tab/source closes.</summary>
internal sealed class DockerFollowSession : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StreamWriter _writer;
    private readonly string _path;
    private readonly Task _worker;
    private int _disposed;

    private DockerFollowSession(DockerOptions options, string path)
    {
        _path = path;
        _writer = new StreamWriter(DockerLogSource.OpenSpool(path), new UTF8Encoding(false), 16384) { AutoFlush = true, NewLine = "\n" };
        _worker = Task.Run(() => RunAsync(options));
    }

    internal static async Task<DockerFollowSession> StartAsync(DockerOptions options, string path, CancellationToken openingToken)
    {
        var session = new DockerFollowSession(options, path);
        try { await session._startup.Task.WaitAsync(openingToken).ConfigureAwait(false); openingToken.ThrowIfCancellationRequested(); return session; }
        catch { session.Dispose(); throw; }
    }

    private async Task RunAsync(DockerOptions options)
    {
        var cursor = new DockerFollowCursor();
        CancellationToken token = _lifetime.Token;
        bool started = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using Process process = DockerCli.StartLogs(cursor.NextAttempt(options));
                    using var cancellation = token.Register(() => DockerCli.Kill(process));
                    var sink = new LogSink(_writer, cursor);
                    Task drain = DockerLogSource.DrainAsync(process, sink, token);
                    try
                    {
                        if (!started)
                        {
                            // A quiet running container is valid. Give immediate CLI
                            // failures time to surface without waiting for its first log.
                            await Task.WhenAny(drain, Task.Delay(250, token)).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            if (drain.IsCompleted)
                            {
                                await drain.ConfigureAwait(false);
                                if (process.ExitCode != 0) throw new IOException(sink.Error("Could not start Docker log follow."));
                            }
                            started = true;
                            _startup.TrySetResult();
                        }
                        await drain.ConfigureAwait(false);
                    }
                    finally
                    {
                        DockerCli.Kill(process);
                        // Observe readers before disposing their process/pipe owners,
                        // including cancellation during the startup grace period.
                        try { await drain.ConfigureAwait(false); }
                        catch (Exception error) when (error is OperationCanceledException or IOException) { }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception error) when (error is IOException or InvalidOperationException)
                {
                    if (!started) { _startup.TrySetException(error); return; }
                    // Docker/daemon outages after startup are retried. Keep the same
                    // source and spool so the host need not replace an active tab.
                }
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { _startup.TrySetException(error); throw; }
        finally { if (!started) _startup.TrySetCanceled(token.IsCancellationRequested ? token : new CancellationToken(true)); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        // Keep UI shutdown bounded. If a platform's pipe teardown is slow, release
        // the writer only after its readers finish; never race writes with disposal.
        try { if (_worker.Wait(TimeSpan.FromSeconds(2))) { Release(); return; } }
        catch (AggregateException) { Release(); return; }
        _ = _worker.ContinueWith(task => { _ = task.Exception; Release(); }, TaskScheduler.Default);
    }

    private void Release()
    {
        try { _writer.Dispose(); }
        finally { _lifetime.Dispose(); DockerLogSource.Delete(_path); }
    }
}

/// <summary>
/// Reconnect from the last RFC3339Nano timestamp, preserving Docker's nanoseconds.
/// Remember occurrence counts at that boundary: identical lines at the same instant
/// may be legitimate repeats, so a set would incorrectly discard new occurrences.
/// </summary>
internal sealed class DockerFollowCursor
{
    private static readonly Regex TimestampPattern = new(@"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{9}Z\z", RegexOptions.CultureInvariant);
    private readonly string _baseline = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffff00Z", System.Globalization.CultureInfo.InvariantCulture);
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private bool _initial = true;
    private string? _last;
    private string? _replayTimestamp;
    private Dictionary<string, int> _replay = new(StringComparer.Ordinal);

    internal DockerOptions NextAttempt(DockerOptions options)
    {
        if (_initial) { _initial = false; return options; }
        _replayTimestamp = _last;
        _replay = new(_counts, StringComparer.Ordinal);
        // 'all' plus --since recovers logs written during an outage. --tail 0 would
        // follow only future output and lose that catch-up window.
        return options with { Tail = "all", Since = _last ?? _baseline, Follow = true };
    }

    internal bool ShouldSkipReplay(string line)
    {
        string? timestamp = Timestamp(line);
        if (timestamp is null || _replayTimestamp is null) return false;
        int comparison = string.CompareOrdinal(timestamp, _replayTimestamp);
        if (comparison < 0) return true;
        if (comparison != 0 || !_replay.TryGetValue(line, out int remaining) || remaining == 0) return false;
        _replay[line] = remaining - 1;
        // Retain replay state for the whole attempt: stdout and stderr can arrive
        // out of order, including boundary lines after a newer timestamp.
        return true;
    }

    internal void Record(string line)
    {
        string? timestamp = Timestamp(line);
        if (timestamp is null) return;
        int comparison = _last is null ? 1 : string.CompareOrdinal(timestamp, _last);
        if (comparison > 0) { _last = timestamp; _counts.Clear(); }
        if (comparison >= 0) _counts[line] = _counts.GetValueOrDefault(line) + 1;
    }

    internal static string? Timestamp(string line)
    {
        int space = line.IndexOf(' ');
        return space > 0 && TimestampPattern.IsMatch(line[..space]) ? line[..space] : null;
    }
}
