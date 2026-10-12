using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Buffers;

namespace LogAce.Plugins.Gcp;

/// <summary>Discovers project IDs through the current gcloud account.</summary>
internal static class GcpProjectCatalog
{
    public static bool TryListProjects(out IReadOnlyList<GcpProjectEntry> projects, out string? error)
    {
        projects = [];
        error = null;

        try
        {
            using Process process = StartProjectsListProcess();
            var (stdout, stderr) = SourceRuntime.ReadResult(process);

            if (process.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(stderr)
                    ? "GCP project listing failed."
                    : $"GCP project listing failed: {stderr}";
                return false;
            }

            projects = stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                .Select(static value => new GcpProjectEntry(value))
                .ToArray();
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not list GCP projects. Ensure the Google Cloud SDK is installed and 'gcloud' is on PATH. {ex.Message}";
            return false;
        }
    }

    private static Process StartProjectsListProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "gcloud",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("projects");
        startInfo.ArgumentList.Add("list");
        startInfo.ArgumentList.Add("--format=value(projectId)");

        Process? process = SourceRuntime.Start(startInfo);
        return process ?? throw new InvalidOperationException("gcloud projects list did not start.");
    }
}
