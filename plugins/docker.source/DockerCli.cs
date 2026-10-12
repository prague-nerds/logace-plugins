using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LogAce.Plugins.Docker;

internal sealed record DockerContainer(string Id, string Name, string State, string Status, string Image);

/// <summary>Process ownership and argument construction shared by discovery and reading.</summary>
internal static class DockerCli
{
    internal static Process Start(string? context, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo("docker")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
        };
        // ArgumentList does not invoke a shell. Contexts, time values and names never
        // become command text; Docker remains responsible for credentials and transport.
        if (!string.IsNullOrWhiteSpace(context)) { info.ArgumentList.Add("--context"); info.ArgumentList.Add(context.Trim()); }
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        try { return Process.Start(info) ?? throw new IOException("Docker CLI did not start."); }
        catch (System.ComponentModel.Win32Exception error) { throw new IOException("Docker CLI could not start. Install Docker and ensure 'docker' is on PATH.", error); }
    }

    internal static Process StartLogs(DockerOptions options)
    {
        var arguments = new List<string> { "logs", "--timestamps", "--tail", options.Tail };
        if (options.Since is not null) { arguments.Add("--since"); arguments.Add(options.Since); }
        if (options.Follow) arguments.Add("--follow");
        arguments.Add("--"); arguments.Add(options.Container);
        return Start(options.Context, arguments);
    }

    internal static async Task<IReadOnlyList<DockerContainer>> ListAsync(string? context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using Process process = Start(context, ["ps", "-a", "--no-trunc", "--format", "{{.ID}}\t{{.Names}}\t{{.State}}\t{{.Status}}\t{{.Image}}"]);
        using var cancellation = token.Register(() => Kill(process));
        Task<string> errors = ReadDiagnosticsAsync(process.StandardError, token);
        var containers = new List<DockerContainer>();
        try
        {
            while (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                string[] fields = line.Split('\t');
                if (fields.Length >= 5 && fields[1].Length > 0) containers.Add(new(fields[0], fields[1], fields[2], fields[3], fields[4]));
                if (containers.Count > 10000) throw new IOException("Too many containers to display. Enter a known container name instead.");
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            string diagnostic = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException(diagnostic.Length == 0 ? "Could not list Docker containers." : diagnostic);
            return containers.GroupBy(container => container.Name, StringComparer.Ordinal).Select(group => group.First())
                .OrderBy(container => container.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally
        {
            Kill(process);
            // Always observe the concurrent reader, including when stdout/validation
            // fails. Otherwise a failed discovery could leave an unobserved task.
            try { await errors.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (IOException) { }
        }
    }

    internal static async Task<string> ReadDiagnosticsAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        char[] buffer = new char[2048];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            if (text.Length < 8192) text.Append(buffer, 0, Math.Min(read, 8192 - text.Length));
        return text.ToString().Trim();
    }

    internal static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
