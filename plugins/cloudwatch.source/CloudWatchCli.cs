using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LogAce.Plugins.CloudWatch;

/// <summary>Owns CLI argument construction; aws owns authentication and transport.</summary>
internal static class CloudWatchCli
{
    internal static Process Start(string? profile, string? region, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo("aws")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
        };
        // Credentials and SSO helpers stay with the user's AWS CLI setup. Never
        // interpolate configuration into shell text or persist access tokens.
        Add(info.ArgumentList, "--profile", profile);
        Add(info.ArgumentList, "--region", region);
        info.ArgumentList.Add("--no-cli-pager");
        info.ArgumentList.Add("--no-cli-auto-prompt");
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        try { return Process.Start(info) ?? throw new IOException("aws did not start."); }
        catch (Win32Exception error)
        {
            throw new IOException("Could not start aws. Install AWS CLI v2 and ensure 'aws' is on PATH.", error);
        }
    }

    internal static Process StartLogs(CloudWatchOptions options)
    {
        // Detailed includes the stream and a timezone-bearing timestamp expected
        // by LogAce's built-in CloudWatch parser. Short omits those fields.
        var arguments = new List<string> { "logs", "tail", options.LogGroup, "--since", options.Since, "--format", "detailed" };
        Add(arguments, "--filter-pattern", options.FilterPattern);
        Add(arguments, "--log-stream-name-prefix", options.StreamFilter?.TrimEnd('*'));
        if (options.Follow) arguments.Add("--follow");
        return Start(options.Profile, options.Region, arguments);
    }

    internal static void Add(ICollection<string> arguments, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        arguments.Add(name); arguments.Add(value);
    }

    internal static string Error(string detail, string fallback)
    {
        if (string.IsNullOrWhiteSpace(detail)) return fallback;
        if (detail.Contains("Unable to locate credentials", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("SSO", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("ExpiredToken", StringComparison.OrdinalIgnoreCase))
            return "AWS authentication is missing or expired. Run 'aws sso login --profile <profile>' or configure AWS credentials. Details: " + detail;
        return fallback + " " + detail;
    }

    internal static async Task<string> ReadDiagnosticsAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        char[] buffer = new char[2048];
        int read;
        // Continue draining after the retained limit to avoid blocking aws on
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
