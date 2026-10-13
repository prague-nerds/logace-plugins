using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text.Json;

namespace LogAce.Plugins.OsLog;

internal static class OsLogFacetCatalog
{
    private const int MaxSampledEvents = 150_000;

    public static string GetFacetLabel(OsLogFacetKind kind)
    {
        return kind == OsLogFacetKind.Subsystem ? "subsystem" : "process";
    }

    public static bool TrySample(
        OsLogFacetKind kind,
        string window,
        out IReadOnlyList<OsLogFacet> facets,
        out string? error)
    {
        facets = [];
        error = null;

        if (!OperatingSystem.IsMacOS())
        {
            error = "macOS unified log requires macOS.";
            return false;
        }

        string normalizedWindow = string.IsNullOrWhiteSpace(window) ? "2m" : window.Trim();

        Process? process;

        try
        {
            process = StartLogShowProcess(normalizedWindow);
        }
        catch (Exception ex)
        {
            error = $"Could not start the 'log' command. (This command is part of macOS.) {ex.Message}";
            return false;
        }

        if (process is null)
        {
            error = "The 'log' command did not start.";
            return false;
        }

        using var ownedProcess = process;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        Task<string> stderrTask = SourceRuntime.ReadDiagnosticsAsync(process.StandardError);

        try
        {
            int parsed = 0;
            string? line;

            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                if (!TryExtractFacet(line, kind, out string? value) || value is null)
                {
                    continue;
                }

                counts[value] = counts.GetValueOrDefault(value) + 1;
                parsed++;

                if (parsed >= MaxSampledEvents)
                {
                    break;
                }
            }
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            try
            {
                process.WaitForExit(1000);
            }
            catch
            {
            }
        }

        if (counts.Count == 0)
        {
            string stderr = SafeResult(stderrTask);

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                error = $"Could not sample the macOS unified log: {stderr}";
                return false;
            }
        }

        facets = counts
            .Select(entry => new OsLogFacet(entry.Key, entry.Value))
            .OrderByDescending(facet => facet.Count)
            .ThenBy(facet => facet.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return true;
    }

    internal static bool TryExtractFacet(string line, OsLogFacetKind kind, out string? value)
    {
        value = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (kind == OsLogFacetKind.Subsystem)
            {
                if (document.RootElement.TryGetProperty("subsystem", out JsonElement subsystem)
                    && subsystem.ValueKind == JsonValueKind.String)
                {
                    string? subsystemValue = subsystem.GetString();

                    if (!string.IsNullOrWhiteSpace(subsystemValue))
                    {
                        value = subsystemValue;
                        return true;
                    }
                }

                return false;
            }

            if (document.RootElement.TryGetProperty("processImagePath", out JsonElement processImagePath)
                && processImagePath.ValueKind == JsonValueKind.String)
            {
                string? imagePath = processImagePath.GetString();

                if (!string.IsNullOrWhiteSpace(imagePath))
                {
                    string name = Path.GetFileName(imagePath!);
                    value = string.IsNullOrWhiteSpace(name) ? imagePath : name;
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Process? StartLogShowProcess(string window)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "log",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("show");
        startInfo.ArgumentList.Add("--style");
        startInfo.ArgumentList.Add("ndjson");
        startInfo.ArgumentList.Add("--last");
        startInfo.ArgumentList.Add(window);
        startInfo.ArgumentList.Add("--info");
        startInfo.ArgumentList.Add("--no-backtrace");

        return SourceRuntime.Start(startInfo);
    }

    private static string SafeResult(Task<string> task)
    {
        try
        {
            return task.GetAwaiter().GetResult().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }
}
