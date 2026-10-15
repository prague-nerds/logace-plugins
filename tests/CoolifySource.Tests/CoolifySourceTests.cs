using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LogAce.PluginApi;
using LogAce.Plugins.Coolify;
using Xunit;

namespace CoolifySource.Tests;

public sealed class CoolifySourceTests
{
    [Fact]
    public void Normalization_keeps_secrets_transient_and_drops_discovery_state()
    {
        var plugin = new CoolifySourcePlugin();
        var config = Config(new() { ["instanceUrl"] = " https://coolify.example/api/v1/ ", ["tail"] = " ALL ", ["search"] = "worker", ["skip"] = 50, ["token"] = "persisted-secret" });
        var normalized = plugin.NormalizeConfiguration(config);
        Assert.Equal("https://coolify.example", normalized.Values.GetProperty("instanceUrl").GetString());
        Assert.Equal("all", normalized.Values.GetProperty("tail").GetString());
        Assert.False(normalized.Values.TryGetProperty("token", out _));
        Assert.False(normalized.Values.TryGetProperty("search", out _));
        Assert.False(normalized.Values.TryGetProperty("skip", out _));
        Assert.Equal("test-secret", normalized.TransientValues!.Value.GetProperty("token").GetString());
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(normalized));
        Assert.True(plugin.ValidateConfiguration(normalized).IsValid);
    }

    [Theory]
    [InlineData("instanceUrl", "file:///tmp/logs")]
    [InlineData("instanceUrl", "https://user:secret@coolify.example")]
    [InlineData("instanceUrl", "https://coolify.example?token=secret")]
    [InlineData("instanceUrl", "https://coolify.example/#secret")]
    [InlineData("applicationUuid", "..")]
    [InlineData("applicationUuid", "app/other")]
    [InlineData("applicationUuid", "")]
    [InlineData("deploymentUuid", "../other")]
    [InlineData("mode", "other")]
    [InlineData("tail", "0")]
    [InlineData("tail", "10001")]
    [InlineData("serviceName", "bad\nname")]
    public void Invalid_settings_are_rejected(string field, string value)
    {
        Assert.False(new CoolifySourcePlugin().ValidateConfiguration(Config(new() { [field] = value })).IsValid);
    }

    [Theory]
    [InlineData("follow", "true")]
    [InlineData("pollSeconds", "3")]
    [InlineData("instanceUrl", 5)]
    [InlineData("pollSeconds", 0)]
    [InlineData("pollSeconds", 301)]
    public void Malformed_saved_types_are_not_replaced_with_defaults(string field, object value)
    {
        Assert.False(new CoolifySourcePlugin().ValidateConfiguration(Config(new() { [field] = value })).IsValid);
    }

    [Fact]
    public void Unsupported_versions_and_invalid_transient_tokens_are_rejected()
    {
        var plugin = new CoolifySourcePlugin();
        var config = Config();
        Assert.False(plugin.ValidateConfiguration(new(2, config.Values, config.TransientValues)).IsValid);
        Assert.False(plugin.ValidateConfiguration(new(1, config.Values, JsonSerializer.SerializeToElement(new { token = 42 }))).IsValid);
        Assert.False(plugin.ValidateConfiguration(new(1, config.Values, JsonSerializer.SerializeToElement(new { token = "secret\r\n" }))).IsValid);
    }

    [Fact]
    public async Task Runtime_snapshot_uses_bearer_auth_timestamps_and_encoded_service_and_cleans_spool()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Json(new { logs = "2026-10-13T10:00:00.123456789Z hello\r\n\n" })));
        var plugin = Plugin(handler);
        using var host = new TempHost();
        var source = await plugin.OpenAsync(Config(new() { ["serviceName"] = "api & worker", ["tail"] = "all" }), new(host));
        Assert.Equal("/api/v1/applications/app-1/logs?lines=all&show_timestamps=true&service_name=api%20%26%20worker", handler.Requests.Single().Path);
        Assert.Equal("Bearer test-secret", handler.Requests.Single().Authorization);
        Assert.Equal("GET", handler.Requests.Single().Method);
        Assert.Equal("2026-10-13T10:00:00.123456789Z hello\n\n", File.ReadAllText(source.BackingFilePath));
        Assert.Equal("docker", source.PreferredProfileId);
        Assert.False(source.AutoReloadOnExternalChange);
        Assert.True(source.SupportsRecentHistory);
        Assert.False(source.SupportsDuplicate);
        Assert.DoesNotContain("test-secret", source.DisplayPath);
        source.Dispose();
        source.Dispose();
        Assert.True(handler.Disposed);
        Assert.False(File.Exists(source.BackingFilePath));
    }

    [Fact]
    public async Task Environment_token_reopens_saved_configuration_without_persisting_the_token()
    {
        string variable = "LOGACE_COOLIFY_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "environment-secret");
        try
        {
            using var handler = new FakeHandler((_, _) => Task.FromResult(Json(new { logs = "" })));
            using var host = new TempHost();
            var config = Config(new() { ["tokenEnvironment"] = variable }, token: null);
            using var source = await Plugin(handler).OpenAsync(config, new(host));
            Assert.Equal("Bearer environment-secret", handler.Requests.Single().Authorization);
            Assert.DoesNotContain("environment-secret", JsonSerializer.Serialize(config));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task Missing_token_fails_before_network_or_spool_creation()
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("Must not send a request."));
        using var host = new TempHost();
        await Assert.ThrowsAsync<ArgumentException>(async () => await Plugin(handler).OpenAsync(Config(new() { ["tokenEnvironment"] = "" }, token: null), new(host)));
        Assert.Empty(handler.Requests);
        Assert.Empty(Directory.GetFiles(host.Directory));
    }

    [Theory]
    [InlineData(401, "API token")]
    [InlineData(403, "read:sensitive")]
    [InlineData(404, "UUID")]
    [InlineData(400, "running")]
    [InlineData(302, "instance URL")]
    public async Task Failed_opens_show_actionable_errors_without_response_secrets(int status, string hint)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("test-secret and other-private-value")
        }));
        using var host = new TempHost();
        var error = await Assert.ThrowsAsync<CoolifyApiException>(async () => await Plugin(handler).OpenAsync(Config(), new(host)));
        Assert.Contains(hint, error.Message);
        Assert.DoesNotContain("test-secret", error.Message);
        Assert.DoesNotContain("other-private-value", error.Message);
        Assert.Empty(Directory.GetFiles(host.Directory));
        Assert.True(handler.Disposed);
    }

    [Fact]
    public async Task Deployment_latest_is_resolved_once_and_pinned_during_follow()
    {
        using var handler = new FakeHandler((request, count) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/app-1")
            ? Json(new { count = 2, deployments = new[] { new { deployment_uuid = "build-1", status = "in_progress", created_at = "2026-10-13", commit = "abc" } } })
            : Json(new { logs = JsonSerializer.Serialize(DeploymentRecords(count > 2 ? 2 : 1)) })));
        using var host = new TempHost();
        using var source = await Plugin(handler).OpenAsync(Config(new() { ["mode"] = "deployment", ["follow"] = true }), new(host));
        await WaitUntil(() => Read(source.BackingFilePath).Contains("message-2"));
        Assert.Contains("/deployment/build-1", source.DisplayPath);
        Assert.Single(handler.Requests, request => request.Path.Contains("/applications/"));
        Assert.Equal("/api/v1/deployments/applications/app-1?skip=0&take=1", handler.Requests[0].Path);
        Assert.All(handler.Requests.Skip(1), request => Assert.Equal("/api/v1/deployments/build-1", request.Path));
        Assert.Equal(1, Read(source.BackingFilePath).Split('\n').Count(line => line.Contains("message-1")));
    }

    [Fact]
    public void Deployment_parser_accepts_encoded_or_decoded_records_and_preserves_multiline_repeats()
    {
        var records = new[]
        {
            new { timestamp = "2026-10-13T10:00:00.000000Z", type = "stderr", output = "same\nsame\n", hidden = true }
        };
        string[] decoded = CoolifyLogParser.Deployment(JsonSerializer.SerializeToElement(records));
        string[] encoded = CoolifyLogParser.Deployment(JsonSerializer.SerializeToElement(JsonSerializer.Serialize(records)));
        Assert.Equal(decoded, encoded);
        Assert.Equal(new[] { "2026-10-13T10:00:00.000000Z [stderr] same", "2026-10-13T10:00:00.000000Z [stderr] same" }, decoded);
        Assert.Empty(CoolifyLogParser.Deployment(JsonSerializer.SerializeToElement<object?>(null)));
        Assert.Equal(new[] { "plain text" }, CoolifyLogParser.Deployment(JsonSerializer.SerializeToElement("plain text\n")));
        Assert.Equal(new[] { "[INFO] Building" }, CoolifyLogParser.Deployment(JsonSerializer.SerializeToElement("[INFO] Building\n")));
    }

    [Fact]
    public async Task Missing_redacted_or_malformed_logs_do_not_silently_open_empty_tabs()
    {
        foreach (object body in new object[] { new { status = "finished" }, new { logs = 5 } })
        {
            using var handler = new FakeHandler((_, _) => Task.FromResult(Json(body)));
            using var host = new TempHost();
            await Assert.ThrowsAsync<CoolifyApiException>(async () => await Plugin(handler).OpenAsync(Config(), new(host)));
            Assert.Empty(Directory.GetFiles(host.Directory));
        }
    }

    [Fact]
    public void Cursor_preserves_occurrences_rolling_windows_empty_polls_and_reports_missing_overlap()
    {
        var cursor = new CoolifyPollCursor();
        Assert.Equal(new[] { "a", "same", "same" }, cursor.Append(["a", "same", "same"], out bool gap));
        Assert.False(gap);
        Assert.Empty(cursor.Append(["a", "same", "same"], out gap));
        Assert.Equal(new[] { "same", "b" }, cursor.Append(["same", "same", "same", "b"], out gap));
        Assert.False(gap);
        Assert.Empty(cursor.Append([], out gap));
        Assert.Empty(cursor.Append(["same", "same", "same", "b"], out gap));
        Assert.Equal(new[] { "after rotation" }, cursor.Append(["after rotation"], out gap));
        Assert.True(gap);
    }

    [Fact]
    public void Cursor_handles_large_repetitive_windows_without_global_line_deduplication()
    {
        var cursor = new CoolifyPollCursor();
        string[] before = Enumerable.Repeat("repeat", 9999).Append("boundary").ToArray();
        cursor.Append(before, out _);
        string[] after = before.Skip(2).Concat(new[] { "repeat", "repeat" }).ToArray();
        Assert.Equal(new[] { "repeat", "repeat" }, cursor.Append(after, out bool gap));
        Assert.False(gap);
    }

    [Fact]
    public async Task Follow_recovers_after_rate_limit_without_replaying_logs_and_ignores_opening_token_after_open()
    {
        using var handler = new FakeHandler((_, count) =>
        {
            if (count == 2)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
                return Task.FromResult(response);
            }
            return Task.FromResult(Json(new { logs = count == 1 ? "t1 same\nt1 same\n" : "t1 same\nt1 same\nt2 backlog\n" }));
        });
        using var host = new TempHost();
        using var opening = new CancellationTokenSource();
        var source = await Plugin(handler).OpenAsync(Config(new() { ["follow"] = true }), new(host, opening.Token));
        opening.Cancel();
        await WaitUntil(() => Read(source.BackingFilePath).Contains("t2 backlog"));
        string content = Read(source.BackingFilePath);
        Assert.Equal(2, content.Split('\n').Count(line => line == "t1 same"));
        Assert.Contains("HTTP 429", content);
        Assert.Contains("connection recovered", content);
        Assert.True(handler.Requests[2].At - handler.Requests[1].At >= TimeSpan.FromSeconds(1.9));
        source.Dispose();
        int count = handler.Requests.Count;
        await Task.Delay(1100);
        Assert.Equal(count, handler.Requests.Count);
        Assert.False(File.Exists(source.BackingFilePath));
    }

    [Theory]
    [InlineData(3, 3, null, 6)]
    [InlineData(3, 40, null, 60)]
    [InlineData(300, 300, null, 300)]
    [InlineData(3, 3, 120, 120)]
    [InlineData(300, 300, 120, 300)]
    public void Retry_backoff_never_shortens_the_configured_poll_interval(int interval, int previous, int? retryAfter, int expected)
    {
        Assert.Equal(TimeSpan.FromSeconds(expected), CoolifyLogSession.RetryDelay(TimeSpan.FromSeconds(interval),
            TimeSpan.FromSeconds(previous), retryAfter is { } seconds ? TimeSpan.FromSeconds(seconds) : null));
    }

    [Fact]
    public async Task Closing_a_tab_cancels_an_inflight_poll_and_removes_the_spool()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FakeHandler(async (_, count, token) =>
        {
            if (count == 1)
            {
                return Json(new { logs = "first\n" });
            }
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            finally
            {
                cancelled.TrySetResult();
            }
            return Json(new { logs = "" });
        });
        using var host = new TempHost();
        var source = await Plugin(handler).OpenAsync(Config(new() { ["follow"] = true }), new(host));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.Dispose();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(File.Exists(source.BackingFilePath));
        Assert.True(handler.Disposed);
    }

    [Fact]
    public async Task Opening_can_be_cancelled_without_leaking_a_client_or_spool()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FakeHandler(async (_, _, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Json(new { logs = "" });
        });
        using var host = new TempHost();
        using var cancellation = new CancellationTokenSource();
        Task<ILogSource> opening = Plugin(handler).OpenAsync(Config(), new(host, cancellation.Token)).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await opening);
        Assert.True(handler.Disposed);
        Assert.Empty(Directory.GetFiles(host.Directory));
    }

    [Fact]
    public async Task Browse_preserves_credentials_and_main_settings_and_clears_stale_deployment()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Json(new[]
        {
            new { uuid = "app-1", name = "API", status = "running", fqdn = "https://api.example" },
            new { uuid = "worker-2", name = "Worker", status = "stopped", fqdn = "" }
        })));
        var plugin = Plugin(handler);
        var host = new FormHost(plugin);
        var configuration = Config(new() { ["deploymentUuid"] = "old-build", ["tail"] = "50" });
        host.Show = request =>
        {
            var browser = Assert.IsType<PluginSourceFormRequest>(request);
            Assert.Equal(2, browser.Form.Fields[0].Choices.Count);
            Assert.Equal("test-secret", browser.InitialConfiguration!.TransientValues!.Value.GetProperty("token").GetString());
            Assert.Equal("50", browser.InitialConfiguration.Values.GetProperty("tail").GetString());
            return Task.FromResult(Selected(plugin, browser.InitialConfiguration, "applicationUuid", "worker-2"));
        };
        var form = Assert.IsType<PluginSourceFormRequest>(plugin.GetConfigurationUi(new(configuration)));
        var update = await form.Form.Actions.Single(action => action.Id == "applications").ExecuteAsync(new(Values(configuration), host, default));
        Assert.Equal("worker-2", update.Values["applicationUuid"].GetString());
        Assert.Equal("Worker", update.Values["applicationName"].GetString());
        Assert.Equal("", update.Values["deploymentUuid"].GetString());
        Assert.False(update.Values.ContainsKey("token"));
        Assert.False(update.Values.ContainsKey("tail"));
        Assert.Equal("Bearer test-secret", handler.Requests.Single().Authorization);
    }

    [Fact]
    public async Task Deployment_browser_reads_paginated_history_and_cancellation_preserves_selection()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Json(new { deployments = new[]
        {
            new { deployment_uuid = "build-2", created_at = "2026-10-13", status = "failed", commit = "abcd" }
        }, count = 100 })));
        var plugin = Plugin(handler);
        var host = new FormHost(plugin);
        var configuration = Config();
        host.Show = async request =>
        {
            var browser = Assert.IsType<PluginSourceFormRequest>(request);
            var values = Values(browser.InitialConfiguration!).Values.ToDictionary(pair => pair.Key, pair => pair.Value);
            values["skip"] = PluginSourceJson.Value(50);
            var update = await browser.Form.Actions.Single().ExecuteAsync(new(new(values), host, default));
            Assert.Equal("build-2", Assert.Single(update.Choices["deploymentUuid"]).Id);
            Assert.Contains("failed", update.Choices["deploymentUuid"][0].Label);
            return new(PluginSourceConfigurationStatus.Cancelled);
        };
        var form = Assert.IsType<PluginSourceFormRequest>(plugin.GetConfigurationUi(new(configuration)));
        var result = await form.Form.Actions.Single(action => action.Id == "deployments").ExecuteAsync(new(Values(configuration), host, default));
        Assert.Empty(result.Values);
        Assert.EndsWith("skip=50&take=50", handler.Requests.Last().Path);
        Assert.All(handler.Requests, request => Assert.Equal("Bearer test-secret", request.Authorization));
    }

    [Fact]
    public async Task Client_rejects_oversized_responses_and_redacts_transport_errors()
    {
        using var handler = new FakeHandler((_, _) =>
        {
            var response = Json(new { logs = "small" });
            response.Content.Headers.ContentLength = 33L * 1024 * 1024;
            return Task.FromResult(response);
        });
        using var host = new TempHost();
        var error = await Assert.ThrowsAsync<CoolifyApiException>(async () => await Plugin(handler).OpenAsync(Config(), new(host)));
        Assert.Contains("32 MiB", error.Message);
        using var failing = new FakeHandler((_, _) => throw new HttpRequestException("test-secret in transport"));
        error = await Assert.ThrowsAsync<CoolifyApiException>(async () => await Plugin(failing).OpenAsync(Config(), new(host)));
        Assert.DoesNotContain("test-secret", error.ToString());
    }

    private static PluginSourceConfiguration Config(Dictionary<string, object>? changes = null, string? token = "test-secret")
    {
        var values = CoolifyConfiguration.Default().Values.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        values["instanceUrl"] = PluginSourceJson.Value("https://coolify.example");
        values["applicationUuid"] = PluginSourceJson.Value("app-1");
        values["follow"] = PluginSourceJson.Value(false);
        values["pollSeconds"] = PluginSourceJson.Value(1);
        foreach (var pair in changes ?? [])
        {
            values[pair.Key] = JsonSerializer.SerializeToElement(pair.Value);
        }
        return new(1, PluginSourceJson.Object(values), token is null ? null : JsonSerializer.SerializeToElement(new { token }));
    }

    private static PluginSourceFormValues Values(PluginSourceConfiguration configuration)
    {
        var values = configuration.Values.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        if (configuration.TransientValues is { } transient)
        {
            foreach (var property in transient.EnumerateObject())
            {
                values[property.Name] = property.Value;
            }
        }
        return new(values);
    }

    private static PluginSourceConfigurationResult Selected(CoolifySourcePlugin plugin, PluginSourceConfiguration initial, string field, string value)
    {
        var values = initial.Values.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        values[field] = PluginSourceJson.Value(value);
        return new(PluginSourceConfigurationStatus.Confirmed, plugin.NormalizeConfiguration(new(1, PluginSourceJson.Object(values), initial.TransientValues)));
    }

    private static CoolifySourcePlugin Plugin(FakeHandler handler) => new((options, token) => new CoolifyClient(options, token, new HttpClient(handler)));

    private static object[] DeploymentRecords(int count) => Enumerable.Range(1, count)
        .Select(i => (object)new { timestamp = $"2026-10-13T10:00:0{i}.000000Z", type = "stdout", output = $"message-{i}", order = i }).ToArray();

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    private static string Read(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> _respond;
        private readonly List<(string Path, string Authorization, string Method, DateTimeOffset At)> _requests = [];

        internal FakeHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond)
            : this((request, count, _) => respond(request, count))
        {
        }

        internal FakeHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        internal IReadOnlyList<(string Path, string Authorization, string Method, DateTimeOffset At)> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToArray();
                }
            }
        }

        internal bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int count;
            lock (_requests)
            {
                _requests.Add((request.RequestUri!.PathAndQuery, request.Headers.Authorization!.ToString(), request.Method.Method, DateTimeOffset.UtcNow));
                count = _requests.Count;
            }
            return _respond(request, count, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class TempHost : IPluginSourceHost, IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "coolify-tests-" + Guid.NewGuid().ToString("N"));

        internal TempHost()
        {
            System.IO.Directory.CreateDirectory(Directory);
        }

        public string CreateSpoolPath(string sourceId, string extension = ".log") => Path.Combine(Directory, Guid.NewGuid() + extension);

        public string GetStatePath(string sourceId, string canonicalDisplayPath) => Path.Combine(Directory, "state.las");

        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }

    private sealed class FormHost(CoolifySourcePlugin plugin) : IPluginSourceUiHost
    {
        internal Func<IPluginSourceUiRequest, Task<PluginSourceConfigurationResult>> Show { get; set; } = _ => Task.FromResult(new PluginSourceConfigurationResult(PluginSourceConfigurationStatus.Cancelled));

        public async ValueTask<PluginSourceConfigurationResult> ShowAsync(IPluginSourceUiRequest request, CancellationToken cancellationToken = default) => await Show(request);

        public ValueTask InvokeAsync(Action update, CancellationToken cancellationToken = default)
        {
            update();
            return ValueTask.CompletedTask;
        }

        public void ReportProgress(string activity)
        {
        }

        public void ReportError(PluginDiagnostic diagnostic)
        {
        }

        public PluginSourceConfigurationResult ConfirmConfiguration(PluginSourceConfiguration configuration) => new(PluginSourceConfigurationStatus.Confirmed, plugin.NormalizeConfiguration(configuration));
    }
}
