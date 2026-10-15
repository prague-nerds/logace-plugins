using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LogAce.Plugins.CloudWatch;

/// <summary>Uses AWS CLI automatic pagination; discovery never requests log contents.</summary>
internal static class CloudWatchGroupCatalog
{
    internal static async Task<IReadOnlyList<string>> ListAsync(string? profile, string? region, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using Process process = CloudWatchCli.Start(profile, region, ["logs", "describe-log-groups", "--query", "logGroups[].logGroupName", "--output", "json"]);
        using var cancellation = token.Register(() => CloudWatchCli.Kill(process));
        // Drain independently; either pipe can exceed an OS pipe buffer.
        Task<string> output = process.StandardOutput.ReadToEndAsync(token);
        Task<string> errors = CloudWatchCli.ReadDiagnosticsAsync(process.StandardError, token);
        try
        {
            await Task.WhenAll(output, errors, process.WaitForExitAsync(token)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException(CloudWatchCli.Error(errors.Result, "Could not list CloudWatch log groups. Check Profile, Region and logs:DescribeLogGroups access, or enter a known group."));
            using var document = JsonDocument.Parse(output.Result);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Expected log group names.");
            var names = new List<string>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())) throw new JsonException("Invalid log group name.");
                names.Add(item.GetString()!);
            }
            return names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        catch { CloudWatchCli.Kill(process); throw; }
    }
}
