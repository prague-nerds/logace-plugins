using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LogAce.PluginApi;

namespace LogAce.Plugins.Coolify;

/// <summary>Owns the HTTP client, temporary spool and an independently cancellable poller.</summary>
internal sealed class CoolifyLogSession : IDisposable
{
    private readonly CoolifyClient _client;
    private readonly CoolifyOptions _options;
    private readonly string _path;
    private readonly StreamWriter _writer;
    private readonly CoolifyPollCursor _cursor = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task _worker = Task.CompletedTask;
    private int _disposed;

    private CoolifyLogSession(CoolifyClient client, CoolifyOptions options, string path)
    {
        _client = client;
        _options = options;
        _path = path;
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete),
            new UTF8Encoding(false))
        {
            NewLine = "\n"
        };
    }

    internal static async Task<ILogSource> OpenAsync(CoolifyClient client, CoolifyOptions options, PluginSourceOpenContext context)
    {
        CoolifyLogSession? session = null;
        string? path = null;
        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (options.Mode == "deployment" && options.DeploymentUuid.Length == 0)
            {
                var deployments = await client.DeploymentsAsync(options.ApplicationUuid, 0, 1, context.CancellationToken).ConfigureAwait(false);
                if (deployments.Count == 0)
                {
                    throw new CoolifyApiException("This application has no deployments. Choose another application or open runtime logs.");
                }
                options = options with
                {
                    DeploymentUuid = deployments[0].Id
                };
            }
            string[] initial = await client.LogsAsync(options, context.CancellationToken).ConfigureAwait(false);
            path = context.Host.CreateSpoolPath("coolify.source");
            session = new(client, options, path);
            await session.WriteSnapshotAsync(initial, context.CancellationToken).ConfigureAwait(false);
            context.CancellationToken.ThrowIfCancellationRequested();
            if (options.Follow)
            {
                session._worker = Task.Run(session.PollAsync);
            }
            return new LogSourceHandle(path, options.DisplayPath, options.ShortName, preferredProfileId: "docker",
                supportsRecentHistory: true, supportsDuplicate: false, autoReloadOnExternalChange: options.Follow,
                lifetime: session);
        }
        catch
        {
            if (session is not null)
            {
                session.Dispose();
            }
            else
            {
                client.Dispose();
                if (path is not null)
                {
                    Delete(path);
                }
            }
            throw;
        }
    }

    private async Task WriteSnapshotAsync(string[] snapshot, CancellationToken token)
    {
        string[] appended = _cursor.Append(snapshot, out bool gap);
        if (gap)
        {
            await StatusAsync("Log snapshots no longer overlap; logs may have rotated, the container changed, or the polling window was exceeded.", token).ConfigureAwait(false);
        }
        foreach (string line in appended)
        {
            await _writer.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
        }
        await _writer.FlushAsync(token).ConfigureAwait(false);
    }

    private async Task PollAsync()
    {
        CancellationToken token = _lifetime.Token;
        TimeSpan interval = TimeSpan.FromSeconds(_options.PollSeconds);
        TimeSpan delay = interval;
        string? lastError = null;
        try
        {
            while (true)
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                string[] snapshot;
                try
                {
                    snapshot = await _client.LogsAsync(_options, token).ConfigureAwait(false);
                }
                catch (IOException error)
                {
                    if (lastError != error.Message)
                    {
                        await StatusAsync(error.Message + " Retrying.", token).ConfigureAwait(false);
                    }
                    lastError = error.Message;
                    delay = RetryDelay(interval, delay, (error as CoolifyApiException)?.RetryAfter);
                    continue;
                }
                if (lastError is not null)
                {
                    await StatusAsync("API connection recovered.", token).ConfigureAwait(false);
                    lastError = null;
                }
                delay = interval;
                await WriteSnapshotAsync(snapshot, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        // A local spool failure is fatal. Observe the task and close the producer;
        // never retry disk errors as if they were remote API outages.
        catch (IOException)
        {
        }
    }

    internal static TimeSpan RetryDelay(TimeSpan interval, TimeSpan previous, TimeSpan? retryAfter) =>
        retryAfter is { } retry ? (retry > interval ? retry : interval)
            : TimeSpan.FromSeconds(Math.Max(interval.TotalSeconds, Math.Min(60, previous.TotalSeconds * 2)));

    private async Task StatusAsync(string message, CancellationToken token)
    {
        string line = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ") + " [Coolify] " + message;
        await _writer.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
        await _writer.FlushAsync(token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _lifetime.Cancel();
        // Bound UI shutdown, keeping the writer alive until the poller has stopped.
        try
        {
            if (_worker.Wait(TimeSpan.FromSeconds(2)))
            {
                Release();
                return;
            }
        }
        catch (AggregateException)
        {
            Release();
            return;
        }
        _ = _worker.ContinueWith(task =>
        {
            _ = task.Exception;
            Release();
        }, TaskScheduler.Default);
    }

    private void Release()
    {
        try
        {
            _writer.Dispose();
        }
        finally
        {
            _client.Dispose();
            _lifetime.Dispose();
            Delete(_path);
        }
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
