using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Buffers;
using LogAce.PluginApi;

namespace LogAce.Plugins.OsLog;

/// <summary>Creates provider captures and canonical identities; the public plugin owns validation and operation scope.</summary>
internal static class OsLogSourceFactory
{
    private const string JsonLinesProfileId = "jsonl";

    public static string GetDisplayPath(OsLogSourceOptions options)
    {
        return BuildDisplayPath(NormalizeOptions(options));
    }

    public static bool TryParseDisplayPath(
        string displayPath,
        out OsLogSourceOptions? options,
        out string? error)
    {
        options = null;
        error = null;

        if (!Uri.TryCreate(displayPath, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, "oslog", StringComparison.OrdinalIgnoreCase))
        {
            error = "Display path is not a macOS unified log source.";
            return false;
        }

        Dictionary<string, string> query = ParseQuery(uri.Query);

        OsLogOpenMode openMode = query.TryGetValue("mode", out string? modeValue)
            && string.Equals(modeValue, "follow", StringComparison.OrdinalIgnoreCase)
                ? OsLogOpenMode.Follow
                : OsLogOpenMode.Snapshot;

        if (IsEnabled(query, "follow"))
        {
            openMode = OsLogOpenMode.Follow;
        }

        OsLogLevel level = query.TryGetValue("level", out string? levelValue)
            ? ParseLevel(levelValue)
            : OsLogLevel.Default;

        string last = query.TryGetValue("last", out string? lastValue) && !string.IsNullOrWhiteSpace(lastValue)
            ? lastValue
            : "15m";

        options = NormalizeOptions(new OsLogSourceOptions(
            Predicate: query.TryGetValue("predicate", out string? predicateValue) ? predicateValue : null,
            Process: query.TryGetValue("process", out string? processValue) ? processValue : null,
            Subsystem: query.TryGetValue("subsystem", out string? subsystemValue) ? subsystemValue : null,
            MinimumLevel: level,
            OpenMode: openMode,
            Last: last));
        return true;
    }

    public static bool TryCreateCore(
        OsLogSourceOptions options,
        out LogSourceHandle? source,
        out string? error)
    {
        source = null;
        error = null;

        if (!OperatingSystem.IsMacOS())
        {
            error = "macOS unified log requires macOS.";
            return false;
        }

        OsLogSourceOptions normalizedOptions = NormalizeOptions(options);

        string spoolPath = BuildSpoolPath(normalizedOptions);
        string displayPath = BuildDisplayPath(normalizedOptions);

        try
        {
            if (normalizedOptions.OpenMode == OsLogOpenMode.Follow)
            {
                if (!OsLogFollowSession.TryStart(normalizedOptions, spoolPath, out OsLogFollowSession? session, out error))
                {
                    return false;
                }

                source = new LogSourceHandle(
                    spoolPath,
                    displayPath,
                    BuildShortName(normalizedOptions),

                    preferredProfileId: JsonLinesProfileId,
                    supportsRecentHistory: true,
                    supportsDuplicate: true,
                    autoReloadOnExternalChange: true,
                    isFileSystemSource: false,
                    lifetime: session);
                return true;
            }

            if (!TryCaptureSnapshot(normalizedOptions, spoolPath, out error))
            {
                TryDeleteFile(spoolPath);
                return false;
            }

            source = new LogSourceHandle(
                spoolPath,
                displayPath,
                BuildShortName(normalizedOptions),

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
            TryDeleteFile(spoolPath);
            error = $"Could not open macOS unified log for '{displayPath}': {ex.Message}";
            return false;
        }
    }

    public static string BuildShortName(OsLogSourceOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Process))
        {
            return $"unified log/{options.Process}";
        }

        if (!string.IsNullOrWhiteSpace(options.Subsystem))
        {
            return $"unified log/{options.Subsystem}";
        }

        return "unified log";
    }

    internal static Process StartLogProcess(OsLogSourceOptions options)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "log",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (options.OpenMode == OsLogOpenMode.Follow)
        {
            startInfo.ArgumentList.Add("stream");
            startInfo.ArgumentList.Add("--style");
            startInfo.ArgumentList.Add("ndjson");
            startInfo.ArgumentList.Add("--level");
            startInfo.ArgumentList.Add(ToStreamLevel(options.MinimumLevel));
        }
        else
        {
            startInfo.ArgumentList.Add("show");
            startInfo.ArgumentList.Add("--style");
            startInfo.ArgumentList.Add("ndjson");
            startInfo.ArgumentList.Add("--last");
            startInfo.ArgumentList.Add(options.Last);
            startInfo.ArgumentList.Add("--no-backtrace");

            if (options.MinimumLevel is OsLogLevel.Info or OsLogLevel.Debug)
            {
                startInfo.ArgumentList.Add("--info");
            }

            if (options.MinimumLevel is OsLogLevel.Debug)
            {
                startInfo.ArgumentList.Add("--debug");
            }
        }

        string? predicate = BuildPredicate(options);

        if (!string.IsNullOrWhiteSpace(predicate))
        {
            startInfo.ArgumentList.Add("--predicate");
            startInfo.ArgumentList.Add(predicate);
        }

        if (!string.IsNullOrWhiteSpace(options.Process))
        {
            startInfo.ArgumentList.Add("--process");
            startInfo.ArgumentList.Add(options.Process!);
        }

        try
        {
            Process? process = SourceRuntime.Start(startInfo);
            return process ?? throw new InvalidOperationException("log process did not start.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Could not start the 'log' command. (This command is part of macOS.) {ex.Message}",
                ex);
        }
    }

    internal static int CopyNdjsonEvents(Stream input, Stream output, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        int writtenEvents = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = reader.ReadLine();

            if (line is null)
            {
                break;
            }

            if (!IsLogEventLine(line))
            {
                continue;
            }

            writer.WriteLine(line);
            writtenEvents++;
        }

        writer.Flush();
        return writtenEvents;
    }

    internal static bool IsLogEventLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("eventType", out JsonElement eventType)
                && eventType.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(eventType.GetString());
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryCaptureSnapshot(
        OsLogSourceOptions options,
        string spoolPath,
        out string? error)
    {
        error = null;

        using Process process = StartLogProcess(options);
        Task<string> stderrTask = SourceRuntime.ReadDiagnosticsAsync(process.StandardError);
        int importedEventCount;

        using (var output = new FileStream(
                   spoolPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            importedEventCount = CopyNdjsonEvents(process.StandardOutput.BaseStream, output, SourceRuntime.OpeningToken);
        }

        process.WaitForExit();
        string stderr = stderrTask.GetAwaiter().GetResult().Trim();

        if (process.ExitCode == 0 && importedEventCount > 0)
        {
            return true;
        }

        if (process.ExitCode == 0 && importedEventCount == 0)
        {
            error = "No matching unified-log events were found. Try a larger 'Last' range or a broader predicate.";
            return false;
        }

        error = string.IsNullOrWhiteSpace(stderr)
            ? $"macOS unified log import failed for '{BuildDisplayPath(options)}'."
            : $"macOS unified log import failed for '{BuildDisplayPath(options)}': {stderr}";
        return false;
    }

    private static string? BuildPredicate(OsLogSourceOptions options)
    {
        string? predicate = NormalizeText(options.Predicate);
        string? subsystem = NormalizeText(options.Subsystem);

        if (subsystem is null)
        {
            return predicate;
        }

        string subsystemClause = $"subsystem == {ToPredicateStringLiteral(subsystem)}";

        if (predicate is null)
        {
            return subsystemClause;
        }

        return $"({predicate}) AND {subsystemClause}";
    }

    private static string ToPredicateStringLiteral(string value)
    {
        return $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static OsLogSourceOptions NormalizeOptions(OsLogSourceOptions options)
    {
        string last = string.IsNullOrWhiteSpace(options.Last) ? "15m" : options.Last.Trim();
        return new OsLogSourceOptions(
            NormalizeText(options.Predicate),
            NormalizeText(options.Process),
            NormalizeText(options.Subsystem),
            options.MinimumLevel,
            options.OpenMode,
            last);
    }

    private static string BuildDisplayPath(OsLogSourceOptions options)
    {
        var queryParts = new List<string>();

        if (options.OpenMode == OsLogOpenMode.Snapshot)
        {
            queryParts.Add($"last={Uri.EscapeDataString(options.Last)}");
        }

        queryParts.Add($"level={ToLevelToken(options.MinimumLevel)}");

        if (!string.IsNullOrWhiteSpace(options.Process))
        {
            queryParts.Add($"process={Uri.EscapeDataString(options.Process!)}");
        }

        if (!string.IsNullOrWhiteSpace(options.Subsystem))
        {
            queryParts.Add($"subsystem={Uri.EscapeDataString(options.Subsystem!)}");
        }

        if (!string.IsNullOrWhiteSpace(options.Predicate))
        {
            queryParts.Add($"predicate={Uri.EscapeDataString(options.Predicate!)}");
        }

        queryParts.Add($"mode={(options.OpenMode == OsLogOpenMode.Follow ? "follow" : "snapshot")}");

        if (options.OpenMode == OsLogOpenMode.Follow)
        {
            queryParts.Add("follow=1");
        }

        return $"oslog://local?{string.Join("&", queryParts)}";
    }

    private static string BuildSpoolPath(OsLogSourceOptions options) => SourceRuntime.CreateSpool(".ndjson");

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(query))
        {
            return result;
        }

        string trimmed = query.StartsWith('?') ? query[1..] : query;

        foreach (string pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separatorIndex = pair.IndexOf('=');

            if (separatorIndex < 0)
            {
                result[Uri.UnescapeDataString(pair)] = string.Empty;
                continue;
            }

            string key = Uri.UnescapeDataString(pair[..separatorIndex]);
            string value = Uri.UnescapeDataString(pair[(separatorIndex + 1)..]);
            result[key] = value;
        }

        return result;
    }

    private static bool IsEnabled(IReadOnlyDictionary<string, string> query, string key)
    {
        return query.TryGetValue(key, out string? value)
            && (value.Length == 0
                || string.Equals(value, "1", StringComparison.Ordinal)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
    }

    private static OsLogLevel ParseLevel(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "info" => OsLogLevel.Info,
            "debug" => OsLogLevel.Debug,
            _ => OsLogLevel.Default
        };
    }

    private static string ToLevelToken(OsLogLevel level)
    {
        return level switch
        {
            OsLogLevel.Info => "info",
            OsLogLevel.Debug => "debug",
            _ => "default"
        };
    }

    private static string ToStreamLevel(OsLogLevel level)
    {
        return level switch
        {
            OsLogLevel.Info => "info",
            OsLogLevel.Debug => "debug",
            _ => "default"
        };
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(
            value
                .Trim()
                .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
                .ToArray());

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "oslog";
        }

        return sanitized.Length <= 64
            ? sanitized
            : sanitized[..64];
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
