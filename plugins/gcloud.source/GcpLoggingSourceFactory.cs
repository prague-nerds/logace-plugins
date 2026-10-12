using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LogAce.PluginApi;

namespace LogAce.Plugins.Gcp;

/// <summary>Creates provider captures and canonical identities; the public plugin owns validation and operation scope.</summary>
internal static class GcpLoggingSourceFactory
{
    private const string JsonLinesProfileId = "jsonl";

    public static string GetDisplayPath(GcpLoggingSourceOptions options)
    {
        return BuildDisplayPath(NormalizeOptions(options, out _));
    }

    public static bool TryParseDisplayPath(
        string displayPath,
        out GcpLoggingSourceOptions? options,
        out string? error)
    {
        options = null;
        error = null;

        if (!Uri.TryCreate(displayPath, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, "gcloud-logging", StringComparison.OrdinalIgnoreCase))
        {
            error = "GCP Cloud Logging display path must start with gcloud-logging://.";
            return false;
        }

        IReadOnlyDictionary<string, string> query = CloudCliProcess.ParseQuery(uri.Query);
        string? project = CloudCliProcess.NullIfDefaultToken(uri.Host);
        string modeText = query.GetValueOrDefault("mode") ?? "snapshot";
        GcpLoggingOpenMode mode = string.Equals(modeText, "follow", StringComparison.OrdinalIgnoreCase)
            || query.ContainsKey("follow")
                ? GcpLoggingOpenMode.Follow
                : GcpLoggingOpenMode.Snapshot;

        int limit = 1000;

        if (query.TryGetValue("limit", out string? limitText)
            && (!int.TryParse(limitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) || limit <= 0))
        {
            error = "GCP Cloud Logging limit must be a positive integer.";
            return false;
        }

        options = NormalizeOptions(
            new GcpLoggingSourceOptions(
                project,
                query.GetValueOrDefault("filter"),
                mode,
                query.GetValueOrDefault("freshness") ?? "1h",
                limit),
            out error);
        return error is null;
    }

    public static bool TryCreateCore(
        GcpLoggingSourceOptions options,
        out LogSourceHandle? source,
        out string? error)
    {
        source = null;
        GcpLoggingSourceOptions normalized = NormalizeOptions(options, out error);

        if (error is not null)
        {
            return false;
        }

        string spoolPath = SourceRuntime.CreateSpool(".ndjson");
        string displayPath = BuildDisplayPath(normalized);

        try
        {
            if (normalized.OpenMode == GcpLoggingOpenMode.Follow)
            {
                if (!CliLineFollowSession.TryStart(
                        displayPath,
                        spoolPath,
                        (_, _) => StartTailProcess(normalized),
                        stderr => BuildGcloudError("gcloud beta logging tail", displayPath, stderr),
                        new JsonStreamToNdjsonTransformer(),
                        extractTimestampRaw: null,
                        out CliLineFollowSession? session,
                        out error))
                {
                    return false;
                }

                source = new LogSourceHandle(
                    spoolPath,
                    displayPath,
                    BuildShortName(normalized),

                    preferredProfileId: JsonLinesProfileId,
                    supportsRecentHistory: true,
                    supportsDuplicate: true,
                    autoReloadOnExternalChange: true,
                    isFileSystemSource: false,
                    lifetime: session);
                return true;
            }

            if (!TryCaptureSnapshot(normalized, spoolPath, out error))
            {
                CloudCliProcess.TryDeleteFile(spoolPath);
                return false;
            }

            source = new LogSourceHandle(
                spoolPath,
                displayPath,
                BuildShortName(normalized),

                preferredProfileId: JsonLinesProfileId,
                supportsRecentHistory: true,
                supportsDuplicate: true,
                autoReloadOnExternalChange: false,
                isFileSystemSource: false,
                lifetime: new TemporaryFileLifetime(spoolPath));
            return true;
        }
        catch (Exception ex)
        {
            CloudCliProcess.TryDeleteFile(spoolPath);
            error = $"Could not open GCP Cloud Logging for '{displayPath}': {ex.Message}";
            return false;
        }
    }

    public static string BuildShortName(GcpLoggingSourceOptions options)
    {
        return string.IsNullOrWhiteSpace(options.Project)
            ? "cloud-logging"
            : $"{options.Project}/cloud-logging";
    }

    public static bool TryNormalizeJsonArrayAsNdjson(string json, out string ndjson, out int recordCount, out string? error)
    {
        ndjson = string.Empty;
        recordCount = 0;
        error = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            var builder = new StringBuilder();

            foreach (string line in JsonStreamToNdjsonTransformer.ToNdjsonLines(document.RootElement))
            {
                builder.AppendLine(line);
                recordCount++;
            }

            ndjson = builder.ToString();
            return true;
        }
        catch (JsonException ex)
        {
            error = $"gcloud returned invalid JSON: {ex.Message}";
            return false;
        }
    }

    private static bool TryCaptureSnapshot(
        GcpLoggingSourceOptions options,
        string spoolPath,
        out string? error)
    {
        error = null;

        using Process process = StartReadProcess(options);
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = SourceRuntime.ReadDiagnosticsAsync(process.StandardError);
        process.WaitForExit();
        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult().Trim();

        if (process.ExitCode != 0)
        {
            error = BuildGcloudError("gcloud logging read", BuildDisplayPath(options), stderr);
            return false;
        }

        if (!TryNormalizeJsonArrayAsNdjson(stdout, out string ndjson, out int recordCount, out error))
        {
            return false;
        }

        if (recordCount == 0)
        {
            error = "No log entries matched the filter in the selected freshness window.";
            return false;
        }

        File.WriteAllText(spoolPath, ndjson, new UTF8Encoding(false));
        return true;
    }

    private static Process StartReadProcess(GcpLoggingSourceOptions options)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "gcloud",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("logging");
        startInfo.ArgumentList.Add("read");

        if (!string.IsNullOrWhiteSpace(options.Filter))
        {
            startInfo.ArgumentList.Add(options.Filter);
        }

        startInfo.ArgumentList.Add("--format=json");
        startInfo.ArgumentList.Add("--limit");
        startInfo.ArgumentList.Add(options.Limit.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--freshness");
        startInfo.ArgumentList.Add(options.Freshness);
        CloudCliProcess.AddOptionalArgument(startInfo, "--project", options.Project);

        try
        {
            Process? process = SourceRuntime.Start(startInfo);
            return process ?? throw new InvalidOperationException("gcloud logging read did not start.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not start 'gcloud'. Ensure the Google Cloud SDK is installed and on PATH. {ex.Message}",
                ex);
        }
    }

    private static Process StartTailProcess(GcpLoggingSourceOptions options)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "gcloud",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("beta");
        startInfo.ArgumentList.Add("logging");
        startInfo.ArgumentList.Add("tail");

        if (!string.IsNullOrWhiteSpace(options.Filter))
        {
            startInfo.ArgumentList.Add(options.Filter);
        }

        startInfo.ArgumentList.Add("--format=json");
        CloudCliProcess.AddOptionalArgument(startInfo, "--project", options.Project);

        try
        {
            Process? process = SourceRuntime.Start(startInfo);
            return process ?? throw new InvalidOperationException("gcloud beta logging tail did not start.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not start 'gcloud'. Ensure the Google Cloud SDK is installed and on PATH. {ex.Message}",
                ex);
        }
    }

    private static GcpLoggingSourceOptions NormalizeOptions(GcpLoggingSourceOptions options, out string? error)
    {
        error = null;
        string freshness = string.IsNullOrWhiteSpace(options.Freshness) ? "1h" : options.Freshness.Trim();

        if (options.Limit <= 0)
        {
            error = "GCP Cloud Logging limit must be a positive integer.";
            return options;
        }

        return new GcpLoggingSourceOptions(
            CloudCliProcess.NormalizeText(options.Project),
            CloudCliProcess.NormalizeText(options.Filter),
            options.OpenMode,
            freshness,
            options.Limit);
    }

    private static string BuildDisplayPath(GcpLoggingSourceOptions options)
    {
        string project = Uri.EscapeDataString(options.Project ?? "default");
        var queryParts = new List<string>();
        CloudCliProcess.AddQueryPart(queryParts, "filter", options.Filter);

        if (options.OpenMode == GcpLoggingOpenMode.Snapshot)
        {
            CloudCliProcess.AddQueryPart(queryParts, "freshness", options.Freshness);
            CloudCliProcess.AddQueryPart(queryParts, "limit", options.Limit.ToString(CultureInfo.InvariantCulture));
        }

        CloudCliProcess.AddQueryPart(queryParts, "mode", options.OpenMode == GcpLoggingOpenMode.Follow ? "follow" : "snapshot");

        if (options.OpenMode == GcpLoggingOpenMode.Follow)
        {
            queryParts.Add("follow=1");
        }

        return $"gcloud-logging://{project}?{string.Join("&", queryParts)}";
    }

    private static string BuildGcloudError(string command, string displayPath, string stderr)
    {
        if (stderr.Contains("Invalid choice: 'beta'", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("component", StringComparison.OrdinalIgnoreCase) && stderr.Contains("beta", StringComparison.OrdinalIgnoreCase))
        {
            return "Live tail requires the gcloud beta component. Run 'gcloud components install beta'.";
        }

        if (stderr.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("You do not currently have an active account", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("credentials", StringComparison.OrdinalIgnoreCase))
        {
            return $"gcloud is not authenticated. Run 'gcloud auth login'. Details: {stderr}";
        }

        return string.IsNullOrWhiteSpace(stderr)
            ? $"{command} failed for '{displayPath}'."
            : $"{command} failed for '{displayPath}': {stderr}";
    }
}
