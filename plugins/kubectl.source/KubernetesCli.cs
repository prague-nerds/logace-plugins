using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LogAce.Plugins.Kubernetes;

/// <summary>Owns CLI argument construction; kubectl owns authentication and transport.</summary>
internal static class KubernetesCli
{
    internal static Process Start(string? context, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo("kubectl")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        // Never interpolate user input into shell text. Kubeconfig, environment and
        // credential helpers are inherited from the user's normal kubectl setup.
        if (!string.IsNullOrWhiteSpace(context))
        {
            info.ArgumentList.Add("--context");
            info.ArgumentList.Add(context);
        }
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        try { return Process.Start(info) ?? throw new IOException("kubectl did not start."); }
        catch (Win32Exception error)
        {
            throw new IOException("Could not start kubectl. Install it and ensure 'kubectl' is on PATH.", error);
        }
    }

    internal static Process StartLogs(KubernetesOptions options)
    {
        var arguments = new List<string> { "logs", "--timestamps=true", "--tail", options.Tail == "all" ? "-1" : options.Tail };
        AddNamespace(arguments, options.Namespace);
        if (options.AllContainers) arguments.Add("--all-containers=true");
        else if (options.Container is not null)
        {
            arguments.Add("--container");
            arguments.Add(options.Container);
        }
        // kubectl disallows using both time flags. Saved SinceTime takes precedence,
        // matching the old built-in configuration and the reconnect cursor.
        if (options.SinceTime is not null)
        {
            arguments.Add("--since-time");
            arguments.Add(options.SinceTime);
        }
        else if (options.Since is not null)
        {
            arguments.Add("--since");
            arguments.Add(options.Since);
        }
        if (options.Follow) arguments.Add("--follow");
        arguments.Add("--");
        arguments.Add(options.Pod);
        return Start(options.Context, arguments);
    }

    internal static void AddNamespace(List<string> arguments, string? namespaceName)
    {
        if (string.IsNullOrWhiteSpace(namespaceName)) return;
        arguments.Add("--namespace");
        arguments.Add(namespaceName);
    }

    internal static async Task<string> ReadDiagnosticsAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        char[] buffer = new char[2048];
        int read;
        // Continue draining after the retained limit to avoid blocking kubectl on
        // a full pipe. Only the error presented to the user is bounded.
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            if (text.Length < 8192) text.Append(buffer, 0, Math.Min(read, 8192 - text.Length));
        return text.ToString().Trim();
    }

    internal static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
}
