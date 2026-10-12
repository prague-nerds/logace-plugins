using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LogAce.PluginApi;

namespace LogAce.Plugins.Kubernetes;

internal static class KubernetesLogSource
{
    internal static async Task<ILogSource> OpenAsync(KubernetesOptions options, PluginSourceOpenContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        string path = context.Host.CreateSpoolPath("kubectl.source");
        IDisposable? lifetime = null;
        try
        {
            lifetime = options.Follow ? await KubernetesFollowSession.StartAsync(options, path, context.CancellationToken).ConfigureAwait(false)
                : await CaptureAsync(options, path, context.CancellationToken).ConfigureAwait(false);
            context.CancellationToken.ThrowIfCancellationRequested();
            var source = new LogSourceHandle(path, options.DisplayPath, options.ShortName, "kubectl",
                supportsRecentHistory: true, supportsDuplicate: false, autoReloadOnExternalChange: options.Follow,
                isFileSystemSource: false, lifetime: lifetime);
            lifetime = null; // Ownership transfers to the returned handle only on success.
            return source;
        }
        catch { lifetime?.Dispose(); Delete(path); throw; }
    }

    private static async Task<IDisposable> CaptureAsync(KubernetesOptions options, string path, CancellationToken token)
    {
        using Process process = KubernetesCli.StartLogs(options);
        using var cancellation = token.Register(() => KubernetesCli.Kill(process));
        using var output = OpenSpool(path);
        using var writer = new StreamWriter(output, new UTF8Encoding(false), 16384) { NewLine = "\n" };
        var sink = new LogSink(writer, null);
        await DrainAsync(process, sink, token).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new IOException(sink.Error("Could not read Kubernetes logs."));
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
            catch { KubernetesCli.Kill(process); throw; }
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
/// kubectl emits pod log data on stdout and CLI diagnostics on stderr. Drain both
/// concurrently so large diagnostics cannot block logs. Only stdout enters the
/// spool; credential and transport errors must not masquerade as log records.
/// </summary>
internal sealed class LogSink(StreamWriter writer, KubernetesFollowCursor? cursor)
{
    private readonly object _sync = new();
    private readonly StringBuilder _diagnostics = new();

    internal async Task PumpAsync(StreamReader reader, bool stderr, CancellationToken token)
    {
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            lock (_sync)
            {
                if (stderr)
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
internal sealed class KubernetesFollowSession : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StreamWriter _writer;
    private readonly string _path;
    private readonly Task _worker;
    private int _disposed;

    private KubernetesFollowSession(KubernetesOptions options, string path)
    {
        _path = path;
        _writer = new StreamWriter(KubernetesLogSource.OpenSpool(path), new UTF8Encoding(false), 16384) { AutoFlush = true, NewLine = "\n" };
        _worker = Task.Run(() => RunAsync(options));
    }

    internal static async Task<KubernetesFollowSession> StartAsync(KubernetesOptions options, string path, CancellationToken openingToken)
    {
        var session = new KubernetesFollowSession(options, path);
        try { await session._startup.Task.WaitAsync(openingToken).ConfigureAwait(false); openingToken.ThrowIfCancellationRequested(); return session; }
        catch { session.Dispose(); throw; }
    }

    private async Task RunAsync(KubernetesOptions options)
    {
        var cursor = new KubernetesFollowCursor();
        CancellationToken token = _lifetime.Token;
        bool started = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using Process process = KubernetesCli.StartLogs(cursor.NextAttempt(options));
                    using var cancellation = token.Register(() => KubernetesCli.Kill(process));
                    var sink = new LogSink(_writer, cursor);
                    Task drain = KubernetesLogSource.DrainAsync(process, sink, token);
                    try
                    {
                        if (!started)
                        {
                            // A quiet running pod is valid. Give immediate CLI
                            // failures time to surface without waiting for its first log.
                            await Task.WhenAny(drain, Task.Delay(250, token)).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            if (drain.IsCompleted)
                            {
                                await drain.ConfigureAwait(false);
                                if (process.ExitCode != 0) throw new IOException(sink.Error("Could not start Kubernetes log follow."));
                            }
                            started = true;
                            _startup.TrySetResult();
                        }
                        await drain.ConfigureAwait(false);
                    }
                    finally
                    {
                        KubernetesCli.Kill(process);
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
                    // Kubernetes/API outages after startup are retried. Keep the same
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
        finally { _lifetime.Dispose(); KubernetesLogSource.Delete(_path); }
    }
}
