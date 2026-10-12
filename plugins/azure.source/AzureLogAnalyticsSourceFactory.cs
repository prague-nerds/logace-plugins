using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Buffers;
using LogAce.PluginApi;

namespace LogAce.Plugins.Azure;

/// <summary>Creates provider captures and canonical identities; the public plugin owns validation and operation scope.</summary>
internal static class AzureLogAnalyticsSourceFactory
{
    private const string JsonLinesProfileId = "jsonl";

    public static string GetDisplayPath(AzureLogAnalyticsOptions options)
    {
        return BuildDisplayPath(NormalizeOptions(options, out _));
    }

    public static bool TryParseDisplayPath(
        string displayPath,
        out AzureLogAnalyticsOptions? options,
        out string? error)
    {
        options = null;
        error = null;

        if (!Uri.TryCreate(displayPath, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, "azure+loganalytics", StringComparison.OrdinalIgnoreCase))
        {
            error = "Azure Log Analytics display path must start with azure+loganalytics://.";
            return false;
        }

        string workspaceId = Uri.UnescapeDataString(uri.Host);
        IReadOnlyDictionary<string, string> query = CloudCliProcess.ParseQuery(uri.Query);
        options = NormalizeOptions(
            new AzureLogAnalyticsOptions(
                workspaceId,
                query.GetValueOrDefault("query") ?? string.Empty,
                query.GetValueOrDefault("timespan") ?? "PT1H"),
            out error);
        return error is null;
    }

    public static bool TryCreateCore(
        AzureLogAnalyticsOptions options,
        out LogSourceHandle? source,
        out string? error)
    {
        source = null;
        AzureLogAnalyticsOptions normalized = NormalizeOptions(options, out error);

        if (error is not null)
        {
            return false;
        }

        string spoolPath = SourceRuntime.CreateSpool(".ndjson");
        string displayPath = BuildDisplayPath(normalized);

        try
        {
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
            error = $"Could not open Azure Log Analytics for '{displayPath}': {ex.Message}";
            return false;
        }
    }

    public static string BuildShortName(AzureLogAnalyticsOptions options)
    {
        string workspace = options.WorkspaceId.Length <= 8 ? options.WorkspaceId : options.WorkspaceId[..8];
        return $"{workspace}/loganalytics";
    }

    public static bool TryNormalizeLogAnalyticsJsonToNdjson(
        string json,
        out string ndjson,
        out int recordCount,
        out string? error)
    {
        ndjson = string.Empty;
        recordCount = 0;
        error = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            var builder = new StringBuilder();
            JsonElement root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("tables", out JsonElement tables)
                && tables.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement table in tables.EnumerateArray())
                {
                    string[] columns = ReadLogAnalyticsColumns(table);

                    if (columns.Length == 0
                        || !table.TryGetProperty("rows", out JsonElement rows)
                        || rows.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (JsonElement row in rows.EnumerateArray())
                    {
                        if (row.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        using var output = new MemoryStream();

                        using (var writer = new Utf8JsonWriter(output))
                        {
                            writer.WriteStartObject();
                            int index = 0;

                            foreach (JsonElement value in row.EnumerateArray())
                            {
                                string columnName = index < columns.Length ? columns[index] : $"Column{index + 1}";
                                writer.WritePropertyName(columnName);
                                value.WriteTo(writer);
                                index++;
                            }

                            writer.WriteEndObject();
                        }

                        builder.AppendLine(Encoding.UTF8.GetString(output.ToArray()));
                        recordCount++;
                    }
                }
            }
            else
            {
                foreach (string line in JsonStreamToNdjsonTransformer.ToNdjsonLines(root))
                {
                    builder.AppendLine(line);
                    recordCount++;
                }
            }

            ndjson = builder.ToString();
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Log Analytics query returned invalid JSON: {ex.Message}";
            return false;
        }
    }

    private static bool TryCaptureSnapshot(
        AzureLogAnalyticsOptions options,
        string spoolPath,
        out string? error)
    {
        error = null;

        using Process process = StartQueryProcess(options);
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = SourceRuntime.ReadDiagnosticsAsync(process.StandardError);
        process.WaitForExit();
        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult().Trim();

        if (process.ExitCode != 0)
        {
            error = AzureAppServiceSourceFactory.BuildAzError("az monitor log-analytics query", BuildDisplayPath(options), stderr);
            return false;
        }

        if (!TryNormalizeLogAnalyticsJsonToNdjson(stdout, out string ndjson, out int recordCount, out error))
        {
            return false;
        }

        if (recordCount == 0)
        {
            error = "No rows were returned for the query.";
            return false;
        }

        File.WriteAllText(spoolPath, ndjson, new UTF8Encoding(false));
        return true;
    }

    private static Process StartQueryProcess(AzureLogAnalyticsOptions options)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "az",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("monitor");
        startInfo.ArgumentList.Add("log-analytics");
        startInfo.ArgumentList.Add("query");
        startInfo.ArgumentList.Add("-w");
        startInfo.ArgumentList.Add(options.WorkspaceId);
        startInfo.ArgumentList.Add("--analytics-query");
        startInfo.ArgumentList.Add(options.Query);
        startInfo.ArgumentList.Add("--timespan");
        startInfo.ArgumentList.Add(options.Timespan);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add("json");

        try
        {
            Process? process = SourceRuntime.Start(startInfo);
            return process ?? throw new InvalidOperationException("az monitor log-analytics query did not start.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not start 'az'. Ensure the Azure CLI is installed and on PATH. {ex.Message}",
                ex);
        }
    }

    private static AzureLogAnalyticsOptions NormalizeOptions(AzureLogAnalyticsOptions options, out string? error)
    {
        error = null;
        string workspaceId = options.WorkspaceId.Trim();
        string query = options.Query.Trim();
        string timespan = string.IsNullOrWhiteSpace(options.Timespan) ? "PT1H" : options.Timespan.Trim();

        if (workspaceId.Length == 0)
        {
            error = "Azure Log Analytics workspace ID cannot be empty.";
            return options;
        }

        if (query.Length == 0)
        {
            error = "Azure Log Analytics KQL query cannot be empty.";
            return options;
        }

        return new AzureLogAnalyticsOptions(workspaceId, query, timespan);
    }

    private static string BuildDisplayPath(AzureLogAnalyticsOptions options)
    {
        string workspace = Uri.EscapeDataString(options.WorkspaceId);
        var queryParts = new List<string>();
        CloudCliProcess.AddQueryPart(queryParts, "query", options.Query);
        CloudCliProcess.AddQueryPart(queryParts, "timespan", options.Timespan);
        return $"azure+loganalytics://{workspace}?{string.Join("&", queryParts)}";
    }

    private static string[] ReadLogAnalyticsColumns(JsonElement table)
    {
        if (!table.TryGetProperty("columns", out JsonElement columns)
            || columns.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return columns
            .EnumerateArray()
            .Select(static column =>
            {
                if (column.ValueKind == JsonValueKind.Object
                    && column.TryGetProperty("name", out JsonElement name)
                    && name.ValueKind == JsonValueKind.String)
                {
                    return name.GetString() ?? string.Empty;
                }

                return string.Empty;
            })
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToArray();
    }
}
