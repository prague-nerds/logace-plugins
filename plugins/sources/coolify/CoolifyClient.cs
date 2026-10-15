using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LogAce.PluginApi;

namespace LogAce.Plugins.Coolify;

internal sealed class CoolifyClient : IDisposable
{
    private const int MaxResponseBytes = 32 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly Uri _apiBase;

    internal CoolifyClient(CoolifyOptions options, string token, HttpClient? http = null)
    {
        // Redirects must not forward credentials or silently change the configured instance.
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _apiBase = options.ApiBase;
    }

    internal async Task<JsonElement> GetAsync(string relativePath, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_apiBase, relativePath));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string hint = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Check the API token and enable API access in Coolify.",
                    HttpStatusCode.Forbidden => "Log access requires a token with read:sensitive and access to this team.",
                    HttpStatusCode.NotFound => "Check the resource UUID, Compose service name and Coolify version.",
                    HttpStatusCode.BadRequest => "Check the settings and whether the application is running.",
                    HttpStatusCode.TooManyRequests => "Coolify rate limit reached; polling will retry after a delay.",
                    _ => "Check the instance URL and Coolify API availability."
                };
                TimeSpan? retry = response.Headers.RetryAfter?.Delta;
                if (retry is null && response.Headers.RetryAfter?.Date is { } date)
                {
                    retry = date - DateTimeOffset.UtcNow;
                }
                if (retry is { } delay)
                {
                    retry = TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 1, 300));
                }
                // Do not expose response bodies: they may include credentials or other secrets.
                throw new CoolifyApiException($"Coolify API returned HTTP {(int)response.StatusCode}. {hint}", retry);
            }
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                throw new CoolifyApiException("Coolify response exceeds 32 MiB. Reduce Last lines or the deployment history page size.");
            }

            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            byte[] buffer = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + read > MaxResponseBytes)
                {
                    throw new CoolifyApiException("Coolify response exceeds 32 MiB. Reduce Last lines or the deployment history page size.");
                }
                bytes.Write(buffer, 0, read);
            }
            using var document = JsonDocument.Parse(bytes.ToArray());
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new CoolifyApiException("Coolify API request timed out. Check instance connectivity.");
        }
        catch (HttpRequestException)
        {
            throw new CoolifyApiException("Could not reach the Coolify API. Check the instance URL, network and TLS certificate.");
        }
        catch (JsonException)
        {
            throw new CoolifyApiException("Coolify returned invalid JSON. Check the instance URL and API version.");
        }
    }

    internal async Task<IReadOnlyList<PluginSourceChoice>> ApplicationsAsync(string search, CancellationToken token)
    {
        JsonElement result = await GetAsync("applications", token).ConfigureAwait(false);
        var applications = Array(result, "applications");
        return applications.Select(app => new PluginSourceChoice(Text(app, "uuid"),
                $"{Text(app, "name")} · {Text(app, "status")} · {Text(app, "fqdn")} · {Text(app, "uuid")}"))
            .Where(choice => choice.Id.Length > 0 && choice.Label.Contains(search, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(choice => choice.Id).OrderBy(choice => choice.Label, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal async Task<IReadOnlyList<PluginSourceChoice>> DeploymentsAsync(string application, int skip, int take, CancellationToken token)
    {
        JsonElement result = await GetAsync($"deployments/applications/{Uri.EscapeDataString(application)}?skip={skip}&take={take}", token).ConfigureAwait(false);
        return Array(result, "deployments").Select(deployment => new PluginSourceChoice(Text(deployment, "deployment_uuid"),
                $"{Text(deployment, "created_at")} · {Text(deployment, "status")} · {Text(deployment, "commit")} · {Text(deployment, "deployment_uuid")}"))
            .Where(choice => choice.Id.Length > 0).DistinctBy(choice => choice.Id).ToArray();
    }

    internal async Task<string[]> LogsAsync(CoolifyOptions options, CancellationToken token)
    {
        string path = options.Mode == "runtime"
            ? $"applications/{Uri.EscapeDataString(options.ApplicationUuid)}/logs?lines={Uri.EscapeDataString(options.Tail)}&show_timestamps=true"
                + (options.ServiceName.Length > 0 ? "&service_name=" + Uri.EscapeDataString(options.ServiceName) : "")
            : "deployments/" + Uri.EscapeDataString(options.DeploymentUuid);
        JsonElement response = await GetAsync(path, token).ConfigureAwait(false);
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("logs", out var logs))
        {
            throw new CoolifyApiException("Coolify did not return logs. Use a token with read:sensitive.");
        }
        return options.Mode == "runtime" ? CoolifyLogParser.Runtime(logs) : CoolifyLogParser.Deployment(logs);
    }

    private static IEnumerable<JsonElement> Array(JsonElement result, string property)
    {
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty(property, out var nested))
        {
            result = nested;
        }
        if (result.ValueKind != JsonValueKind.Array)
        {
            throw new CoolifyApiException("Coolify returned an unexpected resource list. Check the API version.");
        }
        return result.EnumerateArray().ToArray();
    }

    internal static string Text(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString()! : "";

    public void Dispose() => _http.Dispose();
}
