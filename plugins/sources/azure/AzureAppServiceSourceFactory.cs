using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Buffers;
using LogAce.PluginApi;

namespace LogAce.Plugins.Azure;

/// <summary>Creates provider captures and canonical identities; the public plugin owns validation and operation scope.</summary>
internal static class AzureAppServiceSourceFactory
{
    public static string GetDisplayPath(AzureAppServiceOptions options)
    {
        return BuildDisplayPath(NormalizeOptions(options, out _));
    }

    public static bool TryParseDisplayPath(
        string displayPath,
        out AzureAppServiceOptions? options,
        out string? error)
    {
        options = null;
        error = null;

        if (!Uri.TryCreate(displayPath, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, "azure+appservice", StringComparison.OrdinalIgnoreCase))
        {
            error = "Azure App Service display path must start with azure+appservice://.";
            return false;
        }

        string subscription = Uri.UnescapeDataString(uri.Host);
        string[] parts = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

        if (parts.Length < 2)
        {
            error = "Azure App Service display path must include resource group and app name.";
            return false;
        }

        IReadOnlyDictionary<string, string> query = CloudCliProcess.ParseQuery(uri.Query);
        options = NormalizeOptions(
            new AzureAppServiceOptions(
                CloudCliProcess.NullIfDefaultToken(subscription),
                parts[0],
                parts[1],
                query.GetValueOrDefault("slot")),
            out error);
        return error is null;
    }

    public static bool TryCreateCore(
        AzureAppServiceOptions options,
        out LogSourceHandle? source,
        out string? error)
    {
        source = null;
        AzureAppServiceOptions normalized = NormalizeOptions(options, out error);

        if (error is not null)
        {
            return false;
        }

        string spoolPath = SourceRuntime.CreateSpool(".log");
        string displayPath = BuildDisplayPath(normalized);

        try
        {
            if (!CliLineFollowSession.TryStart(
                    displayPath,
                    spoolPath,
                    (_, _) => StartTailProcess(normalized),
                    stderr => BuildAzError("az webapp log tail", displayPath, stderr),
                    new PlainTextOutputTransformer(),
                    extractTimestampRaw: null,
                    out CliLineFollowSession? session,
                    out error))
            {
                return false;
            }

            source = new LogSourceHandle(
                spoolPath,
                displayPath,
                normalized.AppName,

                supportsRecentHistory: true,
                supportsDuplicate: true,
                autoReloadOnExternalChange: true,
                isFileSystemSource: false,
                lifetime: session);
            return true;
        }
        catch (Exception ex)
        {
            CloudCliProcess.TryDeleteFile(spoolPath);
            error = $"Could not open Azure App Service logs for '{displayPath}': {ex.Message}";
            return false;
        }
    }

    private static Process StartTailProcess(AzureAppServiceOptions options)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "az",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("webapp");
        startInfo.ArgumentList.Add("log");
        startInfo.ArgumentList.Add("tail");
        startInfo.ArgumentList.Add("--name");
        startInfo.ArgumentList.Add(options.AppName);
        startInfo.ArgumentList.Add("--resource-group");
        startInfo.ArgumentList.Add(options.ResourceGroup);
        CloudCliProcess.AddOptionalArgument(startInfo, "--subscription", options.Subscription);
        CloudCliProcess.AddOptionalArgument(startInfo, "--slot", options.Slot);

        try
        {
            Process? process = SourceRuntime.Start(startInfo);
            return process ?? throw new InvalidOperationException("az webapp log tail did not start.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not start 'az'. Ensure the Azure CLI is installed and on PATH. {ex.Message}",
                ex);
        }
    }

    private static AzureAppServiceOptions NormalizeOptions(AzureAppServiceOptions options, out string? error)
    {
        error = null;
        string resourceGroup = options.ResourceGroup.Trim();
        string appName = options.AppName.Trim();

        if (resourceGroup.Length == 0)
        {
            error = "Azure resource group cannot be empty.";
            return options;
        }

        if (appName.Length == 0)
        {
            error = "Azure App Service app name cannot be empty.";
            return options;
        }

        return new AzureAppServiceOptions(
            CloudCliProcess.NormalizeText(options.Subscription),
            resourceGroup,
            appName,
            CloudCliProcess.NormalizeText(options.Slot));
    }

    private static string BuildDisplayPath(AzureAppServiceOptions options)
    {
        string subscription = Uri.EscapeDataString(options.Subscription ?? "default");
        string resourceGroup = Uri.EscapeDataString(options.ResourceGroup);
        string app = Uri.EscapeDataString(options.AppName);
        var queryParts = new List<string>();
        CloudCliProcess.AddQueryPart(queryParts, "slot", options.Slot);
        queryParts.Add("follow=1");
        return $"azure+appservice://{subscription}/{resourceGroup}/{app}?{string.Join("&", queryParts)}";
    }

    internal static string BuildAzError(string command, string displayPath, string stderr)
    {
        if (stderr.Contains("az login", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Please run", StringComparison.OrdinalIgnoreCase) && stderr.Contains("login", StringComparison.OrdinalIgnoreCase))
        {
            return $"az is not signed in. Run 'az login'. Details: {stderr}";
        }

        return string.IsNullOrWhiteSpace(stderr)
            ? $"{command} failed for '{displayPath}'."
            : $"{command} failed for '{displayPath}': {stderr}";
    }
}
