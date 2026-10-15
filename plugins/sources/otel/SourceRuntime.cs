using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LogAce.PluginApi;

namespace LogAce.Plugins.Otel;

/// <summary>
/// Adapts the provider's CLI/listener implementation to the public host contract.
/// Each operation has an isolated context; no host paths or global registry are used.
/// Only startup/discovery uses the caller token. Successful live sources own their
/// producer lifetime, so cancelling an old opening operation cannot close a tab.
/// </summary>
internal static class SourceRuntime
{
    private static readonly AsyncLocal<Operation?> Current = new();

    internal static CancellationToken OpeningToken => Current.Value is { Active: true } operation ? operation.Token : default;

    internal static async ValueTask<ILogSource> OpenAsync(PluginSourceOpenContext context, Func<ILogSource> open)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            using var operation = new Operation(context.Host, context.CancellationToken);
            ILogSource? source = null;

            try
            {
                source = open();
                context.CancellationToken.ThrowIfCancellationRequested();
                operation.Commit();
                var result = source;
                source = null;
                return result;
            }
            catch
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            finally
            {
                source?.Dispose();
            }
        }).ConfigureAwait(false);
    }

    internal static async Task<T> DiscoverAsync<T>(CancellationToken token, Func<T> discover)
    {
        token.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            using var operation = new Operation(null, token);

            try
            {
                T result = discover();
                token.ThrowIfCancellationRequested();
                return result;
            }
            catch
            {
                token.ThrowIfCancellationRequested();
                throw;
            }
        }).ConfigureAwait(false);
    }

    internal static string CreateSpool(string extension = ".log")
    {
        var operation = Current.Value ?? throw new InvalidOperationException("Open a source through the plugin API.");
        string path = operation.Host!.CreateSpoolPath("otel.source", extension);
        operation.AddSpool(path);
        return path;
    }

    internal static Process? Start(ProcessStartInfo info)
    {
        OpeningToken.ThrowIfCancellationRequested();
        // Arguments remain separate argv values. The provider owns the CLI's auth
        // environment and credentials; plugin configuration contains no tokens.
        Process? process = Process.Start(info);

        if (process is not null)
        {
            Current.Value?.Track(process);
        }

        return process;
    }

    internal static (string Output, string Error) ReadResult(Process process)
    {
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = ReadDiagnosticsAsync(process.StandardError);

        try
        {
            Task.WhenAll(output, error, process.WaitForExitAsync(OpeningToken)).GetAwaiter().GetResult();
            OpeningToken.ThrowIfCancellationRequested();
            return (output.Result, error.Result.Trim());
        }
        catch
        {
            Kill(process);
            throw;
        }
    }

    internal static async Task<string> ReadDiagnosticsAsync(StreamReader reader)
    {
        var retained = new StringBuilder();
        char[] buffer = new char[2048];
        int read;

        // Keep draining after the retained limit: a full stderr pipe must never
        // block stdout. Diagnostic text is not appended to the log spool.
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            if (retained.Length < 16384)
            {
                retained.Append(buffer, 0, Math.Min(read, 16384 - retained.Length));
            }
        }

        return retained.ToString();
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private sealed class Operation : IDisposable
    {
        private readonly Operation? _previous;
        private readonly object _sync = new();
        private readonly List<CancellationTokenRegistration> _registrations = new();
        private readonly List<string> _spools = new();
        private bool _committed;
        private int _active = 1;

        internal bool Active => Volatile.Read(ref _active) != 0;

        internal IPluginSourceHost? Host
        {
            get;
        }

        internal CancellationToken Token
        {
            get;
        }

        internal Operation(IPluginSourceHost? host, CancellationToken token)
        {
            Host = host;
            Token = token;
            _previous = Current.Value;
            Current.Value = this;
        }

        internal void AddSpool(string path)
        {
            lock (_sync)
            {
                _spools.Add(path);
            }
        }

        internal void Commit() => _committed = true;

        internal void Track(Process process)
        {
            lock (_sync)
            {
                if (Active)
                {
                    _registrations.Add(Token.Register(() => Kill(process)));
                }
            }
        }

        public void Dispose()
        {
            CancellationTokenRegistration[] registrations;

            lock (_sync)
            {
                Volatile.Write(ref _active, 0);
                registrations = _registrations.ToArray();
            }

            foreach (var registration in registrations)
            {
                registration.Dispose();
            }

            if (!_committed)
            {
                foreach (string path in _spools)
                {
                    TemporaryFileLifetime.Delete(path);
                }
            }

            Current.Value = _previous;
        }
    }
}
