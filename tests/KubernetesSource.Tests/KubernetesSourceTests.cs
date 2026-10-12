using System.Diagnostics;
using System.Text.Json;
using LogAce.PluginApi;
using LogAce.Plugins.Kubernetes;
using Xunit;

// PATH is process-wide. Keep CLI fixtures serial; each executable only reads files
// in its own temporary directory and never contacts a real cluster.
[CollectionDefinition("Kubernetes CLI", DisableParallelization = true)]
public sealed class KubernetesCliCollection;

[Collection("Kubernetes CLI")]
public sealed class KubernetesSourceTests
{
    [Fact]
    public void Configuration_preserves_the_legacy_schema_and_rejects_malformed_values()
    {
        var plugin = new KubernetesSourcePlugin();
        var value = plugin.NormalizeConfiguration(Config(" api ", context: " staging ", tail: " ALL ", since: " 30m "));
        Assert.True(plugin.ValidateConfiguration(value).IsValid);
        Assert.Equal("api", value.Values.GetProperty("pod").GetString());
        Assert.Equal("staging", value.Values.GetProperty("context").GetString());
        Assert.Equal("all", value.Values.GetProperty("tail").GetString());
        Assert.Equal("30m", value.Values.GetProperty("since").GetString());
        Assert.False(plugin.ValidateConfiguration(Config("--follow")).IsValid);
        Assert.False(plugin.ValidateConfiguration(Config("api", tail: "-1")).IsValid);
        Assert.False(plugin.ValidateConfiguration(Config("api", context: "x\ny")).IsValid);
        using var document = JsonDocument.Parse("{\"pod\":12,\"tail\":true,\"follow\":\"yes\"}");
        Assert.False(plugin.ValidateConfiguration(plugin.NormalizeConfiguration(new(1, document.RootElement))).IsValid);
        Assert.False(plugin.ValidateConfiguration(new(2, value.Values)).IsValid);
    }

    [Fact]
    public void Reconnect_keeps_nanoseconds_and_occurrence_counts_across_interleaved_output()
    {
        var cursor = new KubernetesFollowCursor();
        var options = new KubernetesOptions("api", null, "default", null, false, "2000", "30m", true);
        Assert.Equal(options, cursor.NextAttempt(options));
        string first = "2026-10-12T10:00:00.123456789Z identical";
        cursor.Record(first); cursor.Record(first);
        var retry = cursor.NextAttempt(options);
        Assert.Equal("all", retry.Tail);
        Assert.Equal("2026-10-12T10:00:00.123456789Z", retry.SinceTime);
        Assert.True(cursor.ShouldSkipReplay(first));
        cursor.Record("2026-10-12T10:00:01.000000001Z newer stdout");
        Assert.True(cursor.ShouldSkipReplay(first)); // delayed stderr boundary replay
        Assert.False(cursor.ShouldSkipReplay(first)); // a legitimate third occurrence
        Assert.True(cursor.ShouldSkipReplay("2026-10-12T09:59:59.999999999Z old"));
        Assert.False(cursor.ShouldSkipReplay("untimestamped application output"));
    }

    [UnixFact]
    public async Task Snapshot_reads_logs_and_excludes_CLI_diagnostics_and_owns_its_spool()
    {
        using var kubectl = new FakeKubectl();
        kubectl.Write("stdout", "2026-10-12T10:00:00.000000001Z hello\n");
        kubectl.Write("stderr", "2026-10-12T10:00:00.000000002Z container error\nCLI warning\n");
        using var host = new TempHost();
        var plugin = new KubernetesSourcePlugin();
        using var source = await plugin.OpenAsync(Config("api; echo unsafe", "remote context", "all", "30m"), new(host));
        string content = await File.ReadAllTextAsync(source.BackingFilePath);
        Assert.Contains("hello", content); Assert.DoesNotContain("container error", content);
        Assert.DoesNotContain("CLI warning", content);
        Assert.Equal("kubectl", source.PreferredProfileId);
        Assert.True(source.SupportsRecentHistory); Assert.False(source.SupportsDuplicate);
        Assert.False(source.AutoReloadOnExternalChange);
        Assert.Contains("kubectl://remote context/default/api; echo unsafe", source.DisplayPath);
        string[] arguments = File.ReadAllLines(kubectl.PathFor("arguments"));
        Assert.Equal(new[] { "--context", "remote context", "logs", "--timestamps=true", "--tail", "-1", "--namespace", "default", "--since", "30m", "--", "api; echo unsafe" }, arguments);
        source.Dispose(); source.Dispose();
        Assert.False(File.Exists(source.BackingFilePath));
    }

    [UnixFact]
    public async Task Snapshot_failure_reports_cli_diagnostic_and_removes_partial_file()
    {
        using var kubectl = new FakeKubectl(); kubectl.Write("stderr", "No such pod: missing\n"); kubectl.Write("exit", "1");
        using var host = new TempHost();
        var error = await Assert.ThrowsAsync<IOException>(async () => await new KubernetesSourcePlugin().OpenAsync(Config("missing"), new(host)));
        Assert.Contains("No such pod", error.Message);
        Assert.Empty(Directory.GetFiles(host.Directory));
    }

    [UnixFact]
    public async Task Snapshot_cancellation_kills_the_process_and_removes_its_spool()
    {
        using var kubectl = new FakeKubectl(); kubectl.Write("hold", "true");
        using var host = new TempHost(); using var cancellation = new CancellationTokenSource();
        Task<ILogSource> opening = new KubernetesSourcePlugin().OpenAsync(Config("api"), new(host, cancellation.Token)).AsTask();
        await WaitUntil(() => File.Exists(kubectl.PathFor("pid")));
        int pid = int.Parse(File.ReadAllText(kubectl.PathFor("pid")));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await opening.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(Directory.GetFiles(host.Directory));
        await WaitUntil(() => !IsRunning(pid));
    }

    [UnixFact]
    public async Task Follow_startup_failure_reports_the_diagnostic_and_releases_the_spool()
    {
        using var kubectl = new FakeKubectl(); kubectl.Write("stderr", "Cannot connect to the Kubernetes API\n"); kubectl.Write("exit", "1");
        using var host = new TempHost();
        var error = await Assert.ThrowsAsync<IOException>(async () => await new KubernetesSourcePlugin().OpenAsync(Config("api", follow: true), new(host)));
        Assert.Contains("Kubernetes API", error.Message);
        Assert.Empty(Directory.GetFiles(host.Directory));
    }

    [UnixFact]
    public async Task Follow_can_cancel_before_the_startup_grace_period_finishes()
    {
        using var kubectl = new FakeKubectl(); kubectl.Write("hold", "true");
        using var host = new TempHost(); using var cancellation = new CancellationTokenSource();
        Task<ILogSource> opening = new KubernetesSourcePlugin().OpenAsync(Config("api", follow: true), new(host, cancellation.Token)).AsTask();
        await WaitUntil(() => File.Exists(kubectl.PathFor("pid")) && File.ReadAllText(kubectl.PathFor("pid")).Length > 0);
        int pid = int.Parse(File.ReadAllText(kubectl.PathFor("pid")));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await opening.WaitAsync(TimeSpan.FromSeconds(5)));
        await WaitUntil(() => !IsRunning(pid) && Directory.GetFiles(host.Directory).Length == 0);
    }

    [UnixFact]
    public async Task Follow_recovers_backlog_deduplicates_replay_and_stops_when_disposed()
    {
        using var kubectl = new FakeKubectl(); kubectl.Write("reconnect", "true");
        using var host = new TempHost(); using var opening = new CancellationTokenSource();
        using var source = await new KubernetesSourcePlugin().OpenAsync(Config("api", follow: true), new(host, opening.Token));
        opening.Cancel(); // the source lifetime must be independent of this token
        await WaitUntil(() => Read(source.BackingFilePath).Contains("during outage"));
        string content = Read(source.BackingFilePath);
        Assert.Equal(3, content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(2, content.Split('\n').Count(line => line.EndsWith("identical")));
        Assert.Contains("--since-time\n2026-10-12T10:00:00.123456789Z", File.ReadAllText(kubectl.PathFor("arguments")));
        Assert.True(source.AutoReloadOnExternalChange);
        int pid = int.Parse(File.ReadAllText(kubectl.PathFor("pid")));
        source.Dispose();
        await WaitUntil(() => !File.Exists(source.BackingFilePath) && !IsRunning(pid));
    }

    [Fact]
    public void Qualified_pods_and_missing_legacy_SinceTime_normalize_without_browser_state()
    {
        var plugin = new KubernetesSourcePlugin();
        using var document = JsonDocument.Parse("""
            {"pod":" jobs/worker ","container":null,"tail":"all","follow":false,"search":"temporary"}
            """);
        var configuration = plugin.NormalizeConfiguration(new(1, document.RootElement));
        Assert.True(plugin.ValidateConfiguration(configuration).IsValid);
        Assert.Equal("worker", configuration.Values.GetProperty("pod").GetString());
        Assert.Equal("jobs", configuration.Values.GetProperty("namespace").GetString());
        Assert.Equal(JsonValueKind.Null, configuration.Values.GetProperty("sinceTime").ValueKind);
        Assert.False(configuration.Values.TryGetProperty("search", out _));
        var conflict = configuration.Values.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        conflict["container"] = PluginSourceJson.Value("app");
        conflict["allContainers"] = PluginSourceJson.Value(true);
        Assert.Contains(plugin.ValidateConfiguration(new(1, PluginSourceJson.Object(conflict))).Errors,
            error => error.FieldId == "container");
    }

    [Fact]
    public void Cursor_orders_variable_fractional_precision_and_retains_raw_nanoseconds()
    {
        var cursor = new KubernetesFollowCursor();
        var options = new KubernetesOptions("api", null, null, null, false, "100", null, true);
        cursor.NextAttempt(options);
        cursor.Record("2026-10-12T10:00:00Z whole second");
        cursor.Record("2026-10-12T10:00:00.9Z later");
        cursor.Record("2026-10-12T10:00:00.123456789Z earlier");
        Assert.Equal("2026-10-12T10:00:00.9Z", cursor.NextAttempt(options).SinceTime);
        Assert.True(cursor.ShouldSkipReplay("2026-10-12T10:00:00.123456789Z earlier"));
        Assert.False(cursor.ShouldSkipReplay("2026-10-12T10:00:00.900000001Z next"));
        Assert.Null(KubernetesFollowCursor.Timestamp("CLI diagnostic without a timestamp"));
    }

    [UnixFact]
    public async Task Log_arguments_preserve_all_containers_and_use_only_one_time_flag()
    {
        using var kubectl = new FakeKubectl();
        using var host = new TempHost();
        var options = new KubernetesOptions("api", "remote", "jobs", null, true, "all", "30m", false,
            "2026-10-12T10:00:00.123456789Z");
        using var source = await new KubernetesSourcePlugin().OpenAsync(KubernetesConfiguration.Create(options), new(host));
        string[] arguments = File.ReadAllLines(kubectl.PathFor("arguments"));
        Assert.Contains("--all-containers=true", arguments);
        Assert.Contains("--since-time", arguments);
        Assert.DoesNotContain("--since", arguments);
        Assert.DoesNotContain("--container", arguments);
        Assert.Equal("kubectl://remote/jobs/api?all-containers=1", source.DisplayPath);
        Assert.Equal("api/*", source.ShortName);
    }

    private const string Pods = """
        {"items":[
          {"metadata":{"namespace":"default","name":"api"},"spec":{"containers":[{"name":"web"}]},
           "status":{"phase":"Running","containerStatuses":[{"ready":true,"restartCount":2}]}},
          {"metadata":{"namespace":"jobs","name":"worker"},"spec":{"containers":[{"name":"job"}],"initContainers":[{"name":"setup"}]},
           "status":{"phase":"Succeeded"}}
        ]}
        """;

    [UnixFact]
    public async Task Pod_browser_filters_and_selects_namespace_without_persisting_discovery_inputs()
    {
        using var kubectl = new FakeKubectl();
        kubectl.Write("pods", Pods);
        var plugin = new KubernetesSourcePlugin();
        var host = new FormHost();
        var main = Assert.IsType<PluginSourceFormRequest>(plugin.GetConfigurationUi(new(Config("api"))));
        host.Show = async request =>
        {
            var browser = Assert.IsType<PluginSourceFormRequest>(request);
            Assert.Equal(2, browser.Form.Fields.Single(field => field.Id == "pod").Choices.Count);
            var values = browser.InitialConfiguration!.Values.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
            values["search"] = PluginSourceJson.Value("job");
            values["runningOnly"] = PluginSourceJson.Value(true);
            var empty = await browser.Form.Actions.Single().ExecuteAsync(new(new(values), host, default));
            Assert.Empty(empty.Choices["pod"]);
            Assert.Single(empty.Diagnostics);
            Assert.Equal("", empty.Values["pod"].GetString());
            values["runningOnly"] = PluginSourceJson.Value(false);
            var found = await browser.Form.Actions.Single().ExecuteAsync(new(new(values), host, default));
            Assert.Equal("jobs/worker", Assert.Single(found.Choices["pod"]).Id);
            values["pod"] = PluginSourceJson.Value("jobs/worker");
            return new(PluginSourceConfigurationStatus.Confirmed, plugin.NormalizeConfiguration(new(1, PluginSourceJson.Object(values))));
        };
        var update = await main.Form.Actions.Single(action => action.Id == "browse-pods").ExecuteAsync(
            new(new(Config("api").Values.EnumerateObject().ToDictionary(p => p.Name, p => p.Value)), host, default));
        Assert.Equal("worker", update.Values["pod"].GetString());
        Assert.Equal("jobs", update.Values["namespace"].GetString());
        Assert.Equal(JsonValueKind.Null, update.Values["container"].ValueKind);
        Assert.Equal(3, update.Values.Count);
    }

    [UnixFact]
    public async Task Container_browser_includes_init_containers_and_clears_all_containers()
    {
        using var kubectl = new FakeKubectl();
        using var document = JsonDocument.Parse(Pods);
        kubectl.Write("pods", document.RootElement.GetProperty("items")[1].GetRawText());
        var plugin = new KubernetesSourcePlugin();
        var config = KubernetesConfiguration.Create(new("worker", "remote", "jobs", null, true, "2000", null, false));
        var host = new FormHost();
        host.Show = request =>
        {
            var browser = Assert.IsType<PluginSourceFormRequest>(request);
            Assert.Equal(new[] { "__default__", "job", "setup" }, browser.Form.Fields.Single().Choices.Select(choice => choice.Id));
            var values = browser.InitialConfiguration!.Values.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
            values["container"] = PluginSourceJson.Value("setup");
            var selected = plugin.NormalizeConfiguration(new(1, PluginSourceJson.Object(values)));
            Assert.True(plugin.ValidateConfiguration(selected).IsValid);
            return ValueTask.FromResult(new PluginSourceConfigurationResult(PluginSourceConfigurationStatus.Confirmed, selected));
        };
        var main = Assert.IsType<PluginSourceFormRequest>(plugin.GetConfigurationUi(new(config)));
        var update = await main.Form.Actions.Single(action => action.Id == "browse-containers").ExecuteAsync(
            new(new(config.Values.EnumerateObject().ToDictionary(p => p.Name, p => p.Value)), host, default));
        Assert.Equal("setup", update.Values["container"].GetString());
        Assert.False(update.Values["allContainers"].GetBoolean());
        Assert.Contains("--namespace\njobs", File.ReadAllText(kubectl.PathFor("arguments")));
    }

    [UnixFact]
    public async Task Pod_listing_is_cancellable_and_scopes_empty_namespace_to_all_namespaces()
    {
        using var kubectl = new FakeKubectl();
        kubectl.Write("hold", "true");
        using var cancellation = new CancellationTokenSource();
        var listing = KubernetesPodCatalog.ListAsync(null, null, cancellation.Token);
        await WaitUntil(() => File.Exists(kubectl.PathFor("arguments")));
        Assert.Contains("--all-namespaces", File.ReadAllLines(kubectl.PathFor("arguments")));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await listing.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [UnixFact]
    public async Task Browse_failure_returns_a_field_diagnostic_and_keeps_manual_inputs()
    {
        using var kubectl = new FakeKubectl();
        kubectl.Write("stderr", "Forbidden: cannot list pods\n");
        kubectl.Write("exit", "1");
        var plugin = new KubernetesSourcePlugin();
        var host = new FormHost();
        var config = Config("known");
        var main = Assert.IsType<PluginSourceFormRequest>(plugin.GetConfigurationUi(new(config)));
        var result = await main.Form.Actions.Single(action => action.Id == "browse-pods").ExecuteAsync(
            new(new(config.Values.EnumerateObject().ToDictionary(p => p.Name, p => p.Value)), host, default));
        Assert.Empty(result.Values);
        Assert.Contains("Forbidden", Assert.Single(result.Diagnostics).Message);
        Assert.Equal(0, host.ShowCount);
    }

    [UnixFact]
    public async Task Discovery_drains_large_diagnostics_without_blocking_the_JSON_pipe()
    {
        using var kubectl = new FakeKubectl();
        kubectl.Write("pods", Pods);
        kubectl.Write("stderr", string.Concat(Enumerable.Repeat("authentication warning\n", 10000)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pods = await KubernetesPodCatalog.ListAsync(null, null, cancellation.Token);
        Assert.Equal(2, pods.Count);
        Assert.Equal(1, pods[0].Ready);
        Assert.Equal(1, pods[0].RegularContainers);
        Assert.Equal(2, pods[0].Restarts);
    }

    [UnixFact]
    public async Task Discovery_rejects_a_valid_JSON_document_with_the_wrong_shape()
    {
        using var kubectl = new FakeKubectl();
        kubectl.Write("pods", "[]");
        var plugin = new KubernetesSourcePlugin();
        var host = new FormHost();
        var configuration = Config("known");
        var main = Assert.IsType<PluginSourceFormRequest>(plugin.GetConfigurationUi(new(configuration)));
        var update = await main.Form.Actions.Single(action => action.Id == "browse-pods").ExecuteAsync(
            new(new(configuration.Values.EnumerateObject().ToDictionary(p => p.Name, p => p.Value)), host, default));
        Assert.Contains("invalid pod list", Assert.Single(update.Diagnostics).Message);
        Assert.Empty(update.Values);
        Assert.Equal(0, host.ShowCount);
    }

    private static PluginSourceConfiguration Config(string container, string? context = null, string tail = "2000", string? since = null, bool follow = false) =>
        KubernetesConfiguration.Create(new(container, context, "default", null, false, tail, since, follow));
    private static string Read(string path) { try { return File.ReadAllText(path); } catch (IOException) { return ""; } }
    private static bool IsRunning(int pid) { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class UnixFactAttribute : FactAttribute
    {
        public UnixFactAttribute() { if (OperatingSystem.IsWindows()) Skip = "The fake Kubernetes executable uses a POSIX shell."; }
    }

    private sealed class TempHost : IPluginSourceHost, IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "kubectl-source-tests-" + Guid.NewGuid().ToString("N"));
        public TempHost() => System.IO.Directory.CreateDirectory(Directory);
        public string CreateSpoolPath(string sourceId, string extension = ".log") => Path.Combine(Directory, Guid.NewGuid().ToString("N") + extension);
        public string GetStatePath(string sourceId, string canonicalDisplayPath) => Path.Combine(Directory, "state.las");
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }

    private sealed class FormHost : IPluginSourceUiHost
    {
        public Func<IPluginSourceUiRequest, ValueTask<PluginSourceConfigurationResult>>? Show { get; set; }
        public int ShowCount { get; private set; }
        public ValueTask<PluginSourceConfigurationResult> ShowAsync(IPluginSourceUiRequest request, CancellationToken cancellationToken = default)
        { ShowCount++; return Show is null ? ValueTask.FromResult(new PluginSourceConfigurationResult(PluginSourceConfigurationStatus.Cancelled)) : Show(request); }
        public ValueTask InvokeAsync(Action update, CancellationToken cancellationToken = default) { update(); return ValueTask.CompletedTask; }
        public void ReportProgress(string activity) { }
        public void ReportError(PluginDiagnostic diagnostic) { }
        public PluginSourceConfigurationResult ConfirmConfiguration(PluginSourceConfiguration configuration) => new(PluginSourceConfigurationStatus.Confirmed, configuration);
    }

    private sealed class FakeKubectl : IDisposable
    {
        private readonly string? _previousPath = Environment.GetEnvironmentVariable("PATH");
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "fake-kubectl-" + Guid.NewGuid().ToString("N"));
        public FakeKubectl()
        {
            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The fake CLI requires a POSIX shell.");
            Directory.CreateDirectory(_directory);
            string executable = PathFor("kubectl");
            File.WriteAllText(executable, """
                #!/bin/sh
                fixture=$(dirname "$0")
                printf '%s\n' "$@" >> "$fixture/arguments"
                printf '%s' "$$" > "$fixture/pid"
                if [ "$1" = "--context" ]; then shift 2; fi
                if [ "$1" = "get" ]; then
                  [ ! -f "$fixture/pods" ] || cat "$fixture/pods"
                elif [ -f "$fixture/reconnect" ]; then
                  if [ ! -f "$fixture/attempt" ]; then
                    touch "$fixture/attempt"
                    printf '%s\n' '2026-10-12T10:00:00.123456789Z identical'
                  else
                    printf '%s\n' '2026-10-12T10:00:00.123456789Z identical' '2026-10-12T10:00:00.123456789Z identical' '2026-10-12T10:00:01.000000001Z during outage'
                    sleep 30
                  fi
                else
                  [ ! -f "$fixture/stdout" ] || cat "$fixture/stdout"
                fi
                [ ! -f "$fixture/stderr" ] || cat "$fixture/stderr" >&2
                [ ! -f "$fixture/hold" ] || sleep 30
                [ ! -f "$fixture/exit" ] || exit "$(cat "$fixture/exit")"
                exit 0
                """);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("PATH", _directory + Path.PathSeparator + _previousPath);
        }
        public string PathFor(string file) => Path.Combine(_directory, file);
        public void Write(string file, string content) => File.WriteAllText(PathFor(file), content);
        public void Dispose() { Environment.SetEnvironmentVariable("PATH", _previousPath); Directory.Delete(_directory, true); }
    }
}
