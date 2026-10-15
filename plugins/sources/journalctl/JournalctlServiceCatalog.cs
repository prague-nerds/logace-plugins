using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Buffers;

namespace LogAce.Plugins.Journalctl;

/// <summary>Discovers systemd services through systemctl.</summary>
internal static class JournalctlServiceCatalog
{
    public static bool TryListServices(
        out IReadOnlyList<JournalctlServiceListEntry> services,
        out string? error)
    {
        services = CreateFallbackEntries();
        error = null;

        try
        {
            using Process process = StartSystemctlListServicesProcess();
            var (stdout, stderr) = SourceRuntime.ReadResult(process);

            if (process.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(stderr)
                    ? "systemctl service listing failed."
                    : $"systemctl service listing failed: {stderr}";
                return false;
            }

            JournalctlServiceListEntry[] listedServices = stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseServiceLine)
                .Where(static item => item is not null)
                .Cast<JournalctlServiceListEntry>()
                .OrderBy(item => item.Unit, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            services = [.. CreateFallbackEntries(), .. listedServices];
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not list systemd services. Ensure 'systemctl' is installed and available on PATH. {ex.Message}";
            return false;
        }
    }

    public static IReadOnlyList<JournalctlServiceListEntry> CreateFallbackEntries()
    {
        return
        [
            new JournalctlServiceListEntry(
                Unit: null,
                Load: string.Empty,
                Active: string.Empty,
                Sub: string.Empty,
                Description: "Read the whole journal across all services.")
        ];
    }

    private static Process StartSystemctlListServicesProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "systemctl",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("list-units");
        startInfo.ArgumentList.Add("--type=service");
        startInfo.ArgumentList.Add("--all");
        startInfo.ArgumentList.Add("--no-legend");
        startInfo.ArgumentList.Add("--no-pager");
        startInfo.ArgumentList.Add("--plain");
        startInfo.ArgumentList.Add("--full");

        Process? process = SourceRuntime.Start(startInfo);
        return process ?? throw new InvalidOperationException("systemctl process did not start.");
    }

    private static JournalctlServiceListEntry? ParseServiceLine(string line)
    {
        string trimmed = line.Trim();

        if (trimmed.Length == 0)
        {
            return null;
        }

        string[] parts = SplitByWhitespace(trimmed, expectedParts: 5);

        if (parts.Length < 5)
        {
            return null;
        }

        return new JournalctlServiceListEntry(
            parts[0],
            parts[1],
            parts[2],
            parts[3],
            parts[4]);
    }

    private static string[] SplitByWhitespace(string value, int expectedParts)
    {
        var parts = new List<string>(expectedParts);
        int index = 0;

        while (index < value.Length && parts.Count < expectedParts - 1)
        {
            while (index < value.Length && char.IsWhiteSpace(value[index]))
            {
                index++;
            }

            if (index >= value.Length)
            {
                break;
            }

            int start = index;

            while (index < value.Length && !char.IsWhiteSpace(value[index]))
            {
                index++;
            }

            parts.Add(value[start..index]);
        }

        while (index < value.Length && char.IsWhiteSpace(value[index]))
        {
            index++;
        }

        parts.Add(index < value.Length ? value[index..] : string.Empty);
        return parts.ToArray();
    }
}
