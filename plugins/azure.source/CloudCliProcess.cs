using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using System.Buffers;

namespace LogAce.Plugins.Azure;

/// <summary>Provider-specific helpers shared by discovery, capture and identity handling within this package.</summary>
internal static class CloudCliProcess
{
    public static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    public static string? NullIfDefaultToken(string? value)
    {
        string? normalized = NormalizeText(Uri.UnescapeDataString(value ?? string.Empty));
        return normalized is null || normalized.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? null
            : normalized;
    }

    public static void AddOptionalArgument(ProcessStartInfo startInfo, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value.Trim());
    }

    public static void AddQueryPart(List<string> queryParts, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        queryParts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value.Trim())}");
    }

    public static IReadOnlyDictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string trimmed = query.StartsWith("?", StringComparison.Ordinal) ? query[1..] : query;

        if (trimmed.Length == 0)
        {
            return values;
        }

        foreach (string part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separatorIndex = part.IndexOf('=');
            string key = separatorIndex < 0 ? part : part[..separatorIndex];
            string value = separatorIndex < 0 ? string.Empty : part[(separatorIndex + 1)..];

            if (key.Length == 0)
            {
                continue;
            }

            values[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
        }

        return values;
    }

    public static string BuildSpoolPath(string directory, string stem, string extension)
    {
        string safeStem = SanitizeFileName(stem);
        string suffix = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        return Path.Combine(directory, $"{safeStem}-{suffix}{extension}");
    }

    public static long CopyOutput(Stream input, Stream output, CancellationToken cancellationToken)
    {
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(16 * 1024);
        long bytesWritten = 0;

        try
        {
            while (true)
            {
                int read = input.Read(buffer, 0, buffer.Length);

                if (read == 0)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                output.Flush();
                bytesWritten += read;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        return bytesWritten;
    }

    public static void TryDeleteFile(string path)
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
            return "cloud-log";
        }

        return sanitized.Length <= 64 ? sanitized : sanitized[..64];
    }
}
