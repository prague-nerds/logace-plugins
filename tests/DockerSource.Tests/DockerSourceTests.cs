using System.Diagnostics;
using System.Text.Json;
using LogAce.PluginApi;
using LogAce.Plugins.Docker;
using Xunit;

// PATH is process-wide. Keep CLI fixtures serial; each executable only reads files
// in its own temporary directory and never contacts a real daemon.
[CollectionDefinition("Docker CLI", DisableParallelization = true)]
public sealed class DockerCliCollection;

[Collection("Docker CLI")]
public sealed class DockerSourceTests
{
    [Fact]
    public void Configuration_preserves_the_legacy_schema_and_rejects_malformed_values()
    {
        var plugin = new DockerSourcePlugin();
        var value = plugin.NormalizeConfiguration(Config(" api ", context: " staging ", tail: " ALL ", since: " 30m "));
        Assert.True(plugin.ValidateConfiguration(value).IsValid);
        Assert.Equal("api", value.Values.GetProperty("container").GetString());
        Assert.Equal("staging", value.Values.GetProperty("context").GetString());
        Assert.Equal("all", value.Values.GetProperty("tail").GetString());
        Assert.Equal("30m", value.Values.GetProperty("since").GetString());
        Assert.False(plugin.ValidateConfiguration(Config("--follow")).IsValid);
        Assert.False(plugin.ValidateConfiguration(Config("api", tail: "-1")).IsValid);
        Assert.False(plugin.ValidateConfiguration(Config("api", context: "x\ny")).IsValid);
        using var document = JsonDocument.Parse("{\"container\":12,\"tail\":true,\"follow\":\"yes\"}");
        Assert.False(plugin.ValidateConfiguration(plugin.NormalizeConfiguration(new(1, document.RootElement))).IsValid);
        Assert.False(plugin.ValidateConfiguration(new(2, value.Values)).IsValid);
    }

    [Fact]
    public void Reconnect_keeps_nanoseconds_and_occurrence_counts_across_both_pipes()
    {
        var cursor = new DockerFollowCursor();
        var options = new DockerOptions("api", null, "2000", "30m", true);
        Assert.Equal(options, cursor.NextAttempt(options));
        string first = "2026-10-12T10:00:00.123456789Z identical";
        cursor.Record(first); cursor.Record(first);
        var retry = cursor.NextAttempt(options);
        Assert.Equal("all", retry.Tail);
        Assert.Equal("2026-10-12T10:00:00.123456789Z", retry.Since);
        Assert.True(cursor.ShouldSkipReplay(first));
        cursor.Record("2026-10-12T10:00:01.000000001Z newer stdout");
        Assert.True(cursor.ShouldSkipReplay(first)); // delayed stderr boundary replay
        Assert.False(cursor.ShouldSkipReplay(first)); // a legitimate third occurrence
        Assert.True(cursor.ShouldSkipReplay("2026-10-12T09:59:59.999999999Z old"));
        Assert.False(cursor.ShouldSkipReplay("untimestamped application output"));
    }

    [UnixFact]
    public async Task Snapshot_reads_stdout_and_container_stderr_and_owns_its_spool()
    {
        using var docker = new FakeDocker();
        docker.Write("stdout", "2026-10-12T10:00:00.000000001Z hello\n");
        docker.Write("stderr", "2026-10-12T10:00:00.000000002Z container error\nCLI warning\n");
        using var host = new TempHost();
        var plugin = new DockerSourcePlugin();
        using var source = await plugin.OpenAsync(Config("api; echo unsafe", "remote context", "all", "30m"), new(host));
        string content = await File.ReadAllTextAsync(source.BackingFilePath);
        Assert.Contains("hello", content); Assert.Contains("container error", content);
        Assert.DoesNotContain("CLI warning", content);
        Assert.Equal("docker", source.PreferredProfileId);
        Assert.True(source.SupportsRecentHistory); Assert.False(source.SupportsDuplicate);
        Assert.False(source.AutoReloadOnExternalChange);
        Assert.Contains("docker://remote%20context/api%3B%20echo%20unsafe", source.DisplayPath);
        string[] arguments = File.ReadAllLines(docker.PathFor("arguments"));
        Assert.Equal(new[] { "--context", "remote context", "logs", "--timestamps", "--tail", "all", "--since", "30m", "--", "api; echo unsafe" }, arguments);
        source.Dispose(); source.Dispose();
        Assert.False(File.Exists(source.BackingFilePath));
    }

    [UnixFact]
    public async Task Snapshot_failure_reports_cli_diagnostic_and_removes_partial_file()
    {
        using var docker = new FakeDocker(); docker.Write("stderr", "No such container: missing\n"); docker.Write("exit", "1");
        using var host = new TempHost();
        var error = await Assert.ThrowsAsync<IOException>(async () => await new DockerSourcePlugin().OpenAsync(Config("missing"), new(host)));
        Assert.Contains("No such container", error.Message);
        Assert.Empty(Directory.GetFiles(host.Directory));
    }

    [UnixFact]
    public async Task Snapshot_cancellation_kills_the_process_and_removes_its_spool()
    {
        using var docker = new FakeDocker(); docker.Write("hold", "true");
        using var host = new TempHost(); using var cancellation = new CancellationTokenSource();
        Task<ILogSource> opening = new DockerSourcePlugin().OpenAsync(Config("api"), new(host, cancellation.Token)).AsTask();
        await WaitUntil(() => File.Exists(docker.PathFor("pid")));
        int pid = int.Parse(File.ReadAllText(docker.PathFor("pid")));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await opening.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(Directory.GetFiles(host.Directory));
        await WaitUntil(() => !IsRunning(pid));
    }

    [UnixFact]
    public async Task Follow_startup_failure_reports_the_diagnostic_and_releases_the_spool()
    {
        using var docker = new FakeDocker(); docker.Write("stderr", "Cannot connect to the Docker daemon\n"); docker.Write("exit", "1");
        using var host = new TempHost();
        var error = await Assert.ThrowsAsync<IOException>(async () => await new DockerSourcePlugin().OpenAsync(Config("api", follow: true), new(host)));
        Assert.Contains("Docker daemon", error.Message);
        Assert.Empty(Directory.GetFiles(host.Directory));
    }

    [UnixFact]
    public async Task Follow_can_cancel_before_the_startup_grace_period_finishes()
    {
        using var docker = new FakeDocker(); docker.Write("hold", "true");
        using var host = new TempHost(); using var cancellation = new CancellationTokenSource();
        Task<ILogSource> opening = new DockerSourcePlugin().OpenAsync(Config("api", follow: true), new(host, cancellation.Token)).AsTask();
        await WaitUntil(() => File.Exists(docker.PathFor("pid")) && File.ReadAllText(docker.PathFor("pid")).Length > 0);
        int pid = int.Parse(File.ReadAllText(docker.PathFor("pid")));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await opening.WaitAsync(TimeSpan.FromSeconds(5)));
        await WaitUntil(() => !IsRunning(pid) && Directory.GetFiles(host.Directory).Length == 0);
    }

    [UnixFact]
    public async Task Follow_recovers_backlog_deduplicates_replay_and_stops_when_disposed()
    {
        using var docker = new FakeDocker(); docker.Write("reconnect", "true");
        using var host = new TempHost(); using var opening = new CancellationTokenSource();
        using var source = await new DockerSourcePlugin().OpenAsync(Config("api", follow: true), new(host, opening.Token));
        opening.Cancel(); // the source lifetime must be independent of this token
        await WaitUntil(() => Read(source.BackingFilePath).Contains("during outage"));
        string content = Read(source.BackingFilePath);
        Assert.Equal(3, content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(2, content.Split('\n').Count(line => line.EndsWith("identical")));
        Assert.Contains("--tail\nall\n--since\n2026-10-12T10:00:00.123456789Z", File.ReadAllText(docker.PathFor("arguments")));
        Assert.True(source.AutoReloadOnExternalChange);
        int pid = int.Parse(File.ReadAllText(docker.PathFor("pid")));
        source.Dispose();
        await WaitUntil(() => !File.Exists(source.BackingFilePath) && !IsRunning(pid));
    }

    [UnixFact]
    public async Task Browse_filters_containers_and_does_not_persist_discovery_inputs()
    {
        using var docker = new FakeDocker();
        docker.Write("containers", "aaa\tapi\trunning\tUp 3 minutes\tweb:latest\nbbb\tworker\texited\tExited (0)\tjob:latest\n");
        var plugin = new DockerSourcePlugin(); var host = new FormHost();
        var main = Assert.IsType<PluginSourceFormRequest>(plugin.GetConfigurationUi(new(Config("api"))));
        host.Show = async request =>
        {
            var browser = Assert.IsType<PluginSourceFormRequest>(request);
            var field = browser.Form.Fields.Single(field => field.Id == "container");
            Assert.Equal(2, field.Choices.Count);
            var values = browser.InitialConfiguration!.Values.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
            values["search"] = PluginSourceJson.Value("job"); values["runningOnly"] = PluginSourceJson.Value(true);
            var refresh = browser.Form.Actions.Single();
            var empty = await refresh.ExecuteAsync(new(new(values), host, default));
            Assert.Empty(empty.Choices["container"]); Assert.Single(empty.Diagnostics);
            Assert.Equal("", empty.Values["container"].GetString());
            values["runningOnly"] = PluginSourceJson.Value(false);
            var found = await refresh.ExecuteAsync(new(new(values), host, default));
            Assert.Equal("worker", Assert.Single(found.Choices["container"]).Id);
            values["container"] = PluginSourceJson.Value("worker");
            return new(PluginSourceConfigurationStatus.Confirmed, plugin.NormalizeConfiguration(new(1, PluginSourceJson.Object(values))));
        };
        var selected = await main.Form.Actions.Single().ExecuteAsync(new(new(Config("api").Values.EnumerateObject().ToDictionary(p => p.Name, p => p.Value)), host, default));
        Assert.Equal("worker", selected.Values["container"].GetString());
        Assert.Single(selected.Values);
    }

    [UnixFact]
    public async Task Browse_failure_is_actionable_and_keeps_the_manual_configuration()
    {
        using var docker = new FakeDocker(); docker.Write("stderr", "Cannot connect to the Docker daemon\n"); docker.Write("exit", "1");
        var plugin = new DockerSourcePlugin(); var host = new FormHost();
        var request = Assert.IsType<PluginSourceFormRequest>(plugin.GetConfigurationUi(new(Config("known"))));
        var result = await request.Form.Actions.Single().ExecuteAsync(new(new(Config("known").Values.EnumerateObject().ToDictionary(p => p.Name, p => p.Value)), host, default));
        Assert.Empty(result.Values); Assert.Contains("Docker daemon", Assert.Single(result.Diagnostics).Message);
        Assert.Equal(0, host.ShowCount);
    }

    private static PluginSourceConfiguration Config(string container, string? context = null, string tail = "2000", string? since = null, bool follow = false) =>
        DockerConfiguration.Create(container, context, tail, since, follow);
    private static string Read(string path) { try { return File.ReadAllText(path); } catch (IOException) { return ""; } }
    private static bool IsRunning(int pid) { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class UnixFactAttribute : FactAttribute
    {
        public UnixFactAttribute() { if (OperatingSystem.IsWindows()) Skip = "The fake Docker executable uses a POSIX shell."; }
    }

    private sealed class TempHost : IPluginSourceHost, IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "docker-source-tests-" + Guid.NewGuid().ToString("N"));
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

    private sealed class FakeDocker : IDisposable
    {
        private readonly string? _previousPath = Environment.GetEnvironmentVariable("PATH");
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "fake-docker-" + Guid.NewGuid().ToString("N"));
        public FakeDocker()
        {
            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The fake CLI requires a POSIX shell.");
            Directory.CreateDirectory(_directory);
            string executable = PathFor("docker");
            File.WriteAllText(executable, """
                #!/bin/sh
                fixture=$(dirname "$0")
                printf '%s\n' "$@" >> "$fixture/arguments"
                printf '%s' "$$" > "$fixture/pid"
                if [ "$1" = "--context" ]; then shift 2; fi
                if [ "$1" = "ps" ]; then
                  [ ! -f "$fixture/containers" ] || cat "$fixture/containers"
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
