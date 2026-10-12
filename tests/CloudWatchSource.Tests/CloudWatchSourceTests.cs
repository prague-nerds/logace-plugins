using System.Diagnostics;
using System.Text.Json;
using LogAce.PluginApi;
using LogAce.Plugins.CloudWatch;
using Xunit;

// PATH is process-wide. Keep CLI fixtures serial; each executable only reads files
// in its own temporary directory and never contacts AWS.
[CollectionDefinition("CloudWatch CLI", DisableParallelization = true)]
public sealed class CloudWatchCliCollection;

[Collection("CloudWatch CLI")]
public sealed class CloudWatchSourceTests
{

    [Fact]
    public void Legacy_numeric_mode_and_form_checkbox_normalize_to_the_same_v1_payload()
    {
        var plugin = new CloudWatchSourcePlugin();
        var config = plugin.NormalizeConfiguration(Config(" /aws/lambda/api ", " prod ", " 30m ", true));
        Assert.True(plugin.ValidateConfiguration(config).IsValid);
        Assert.Equal("/aws/lambda/api", config.Values.GetProperty("logGroup").GetString());
        Assert.Equal("prod", config.Values.GetProperty("profile").GetString());
        Assert.Equal(1, config.Values.GetProperty("openMode").GetInt32());
        var ui = Assert.IsType<PluginSourceFormRequest>(plugin.GetConfigurationUi(new(config)));
        Assert.True(ui.InitialConfiguration!.Values.GetProperty("follow").GetBoolean());
        Assert.Equal(config.Values.GetRawText(), plugin.NormalizeConfiguration(ui.InitialConfiguration).Values.GetRawText());
        Assert.False(plugin.ValidateConfiguration(Config("--follow")).IsValid);
        Assert.False(plugin.ValidateConfiguration(Config("api", since: "5h30m")).IsValid);
        Assert.False(plugin.ValidateConfiguration(Config("api", profile: "x\ny")).IsValid);
        using var malformed = JsonDocument.Parse("{\"logGroup\":12,\"openMode\":true,\"since\":false}");
        Assert.False(plugin.ValidateConfiguration(plugin.NormalizeConfiguration(new(1, malformed.RootElement))).IsValid);
        Assert.False(plugin.ValidateConfiguration(new(2, config.Values)).IsValid);
    }

    [Fact]
    public void Replay_retains_occurrences_out_of_order_and_suppresses_multiline_continuations()
    {
        var cursor = new CloudWatchFollowCursor(); var options = CloudWatchConfiguration.Read(Config("api", follow: true));
        Assert.Equal(options, cursor.NextAttempt(options));
        string first = "2026-10-12T10:00:00.123+00:00 stream identical";
        string earlier = "2026-10-12T09:59:59.900+00:00 other delayed";
        cursor.Record(first); cursor.Record(first); cursor.Record(earlier);
        Assert.Equal("2026-10-12T09:59:59.8990000+00:00", cursor.NextAttempt(options).Since);
        Assert.True(cursor.ShouldSkipReplay(first));
        Assert.True(cursor.ShouldSkipReplay("same continuation"));
        Assert.True(cursor.ShouldSkipReplay(earlier));
        Assert.True(cursor.ShouldSkipReplay(first));
        Assert.False(cursor.ShouldSkipReplay(first)); // legitimate third occurrence
        Assert.False(cursor.ShouldSkipReplay("new continuation"));
        Assert.False(cursor.ShouldSkipReplay("2026-10-12T09:59:58.000Z stream newly delayed"));
        Assert.Null(CloudWatchFollowCursor.Timestamp("not a timestamp"));
        var quiet = new CloudWatchFollowCursor(); quiet.NextAttempt(options);
        Assert.Equal(options.Since, quiet.NextAttempt(options).Since);
    }

    [UnixFact]
    public async Task Empty_snapshot_and_authentication_error_remove_the_spool()
    {
        using var aws = new FakeAws(); using var host = new TempHost();
        var empty = await Assert.ThrowsAsync<IOException>(async () => await new CloudWatchSourcePlugin().OpenAsync(Config("api"), new(host)));
        Assert.Contains("No matching", empty.Message); Assert.Empty(Directory.GetFiles(host.Directory));
        aws.Write("stderr", "Unable to locate credentials"); aws.Write("exit", "1");
        var error = await Assert.ThrowsAsync<IOException>(async () => await new CloudWatchSourcePlugin().OpenAsync(Config("api"), new(host)));
        Assert.Contains("aws sso login", error.Message); Assert.Empty(Directory.GetFiles(host.Directory));
    }

    [UnixFact]
    public async Task Browse_filters_refreshes_clears_stale_selection_and_preserves_main_settings()
    {
        using var aws = new FakeAws(); aws.Write("groups", "[\"/aws/api\",\"/aws/jobs\",\"/aws/api\"]");
        var plugin = new CloudWatchSourcePlugin(); var config = Config("/aws/api", "prod", "30m", true); var host = new FormHost();
        host.Show = async request =>
        {
            var browser = Assert.IsType<PluginSourceFormRequest>(request);
            Assert.Equal(2, browser.Form.Fields[0].Choices.Count);
            var values = browser.InitialConfiguration!.Values.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
            values["search"] = PluginSourceJson.Value("missing");
            var empty = await browser.Form.Actions.Single().ExecuteAsync(new(new(values), host, default));
            Assert.Empty(empty.Choices["logGroup"]); Assert.Single(empty.Diagnostics); Assert.Equal("", empty.Values["logGroup"].GetString());
            values["search"] = PluginSourceJson.Value("JOBS");
            var found = await browser.Form.Actions.Single().ExecuteAsync(new(new(values), host, default));
            Assert.Equal("/aws/jobs", Assert.Single(found.Choices["logGroup"]).Id);
            values["logGroup"] = PluginSourceJson.Value("/aws/jobs");
            var selected = plugin.NormalizeConfiguration(new(1, PluginSourceJson.Object(values)));
            Assert.Equal(1, selected.Values.GetProperty("openMode").GetInt32());
            Assert.Equal("30m", selected.Values.GetProperty("since").GetString());
            Assert.Equal("ERROR", selected.Values.GetProperty("filterPattern").GetString());
            Assert.False(selected.Values.TryGetProperty("search", out _));
            Assert.True(plugin.ValidateConfiguration(selected).IsValid);
            return new(PluginSourceConfigurationStatus.Confirmed, selected);
        };
        var main = Assert.IsType<PluginSourceFormRequest>(plugin.GetConfigurationUi(new(config)));
        var update = await main.Form.Actions.Single().ExecuteAsync(new(new(config.Values.EnumerateObject().ToDictionary(p => p.Name,p => p.Value)), host, default));
        Assert.Equal("/aws/jobs", Assert.Single(update.Values).Value.GetString());
        Assert.Contains("--profile\nprod\n--region\neu-central-1", File.ReadAllText(aws.PathFor("arguments")));
        Assert.DoesNotContain("--no-paginate", File.ReadAllText(aws.PathFor("arguments")));
    }

    [UnixFact]
    public async Task Listing_drains_large_diagnostics_and_rejects_wrong_JSON_shapes()
    {
        using var aws = new FakeAws(); aws.Write("groups", "[\"/aws/api\"]");
        aws.Write("stderr", string.Concat(Enumerable.Repeat("warning\n",10000)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Single(await CloudWatchGroupCatalog.ListAsync(null,null,timeout.Token));
        aws.Write("groups", "{}");
        await Assert.ThrowsAsync<JsonException>(() => CloudWatchGroupCatalog.ListAsync(null,null,timeout.Token));
    }

    [UnixFact]
    public async Task Browse_errors_keep_manual_values_and_explain_recovery()
    {
        using var aws = new FakeAws(); aws.Write("stderr","AccessDenied: no DescribeLogGroups access"); aws.Write("exit","1");
        var plugin = new CloudWatchSourcePlugin(); var config = Config("known"); var host = new FormHost();
        var main = Assert.IsType<PluginSourceFormRequest>(plugin.GetConfigurationUi(new(config)));
        var update = await main.Form.Actions.Single().ExecuteAsync(new(new(config.Values.EnumerateObject().ToDictionary(p => p.Name,p => p.Value)),host,default));
        Assert.Empty(update.Values); Assert.Equal(0,host.ShowCount);
        Assert.Contains("enter a known group",Assert.Single(update.Diagnostics).Message);
        Assert.Contains("AccessDenied",update.Diagnostics[0].Message);
    }

    [UnixFact]
    public async Task Listing_cancellation_kills_the_child()
    {
        using var aws = new FakeAws(); aws.Write("hold","true"); using var token = new CancellationTokenSource();
        var task = CloudWatchGroupCatalog.ListAsync(null,null,token.Token);
        await WaitUntil(() => File.Exists(aws.PathFor("pid")) && Read(aws.PathFor("pid")).Length > 0);
        int pid = int.Parse(Read(aws.PathFor("pid"))); token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task.WaitAsync(TimeSpan.FromSeconds(5)));
        await WaitUntil(() => !IsRunning(pid));
    }
    [UnixFact]
    public async Task Snapshot_reads_logs_and_excludes_CLI_diagnostics_and_owns_its_spool()
    {
        using var aws = new FakeAws();
        aws.Write("stdout", "2026-10-12T10:00:00.001+00:00 stream hello\n");
        aws.Write("stderr", "2026-10-12T10:00:00.002+00:00 stream container error\nCLI warning\n");
        using var host = new TempHost();
        var plugin = new CloudWatchSourcePlugin();
        using var source = await plugin.OpenAsync(Config("api; echo unsafe", "remote profile", "30m"), new(host));
        string content = await File.ReadAllTextAsync(source.BackingFilePath);
        Assert.Contains("hello", content); Assert.DoesNotContain("container error", content);
        Assert.DoesNotContain("CLI warning", content);
        Assert.Equal("cloudwatch", source.PreferredProfileId);
        Assert.True(source.SupportsRecentHistory); Assert.True(source.SupportsDuplicate);
        Assert.False(source.AutoReloadOnExternalChange);
        Assert.Contains("cloudwatch://remote%20profile@eu-central-1/", source.DisplayPath);
        string[] arguments = File.ReadAllLines(aws.PathFor("arguments"));
        Assert.Equal(new[] { "--profile", "remote profile", "--region", "eu-central-1", "--no-cli-pager", "--no-cli-auto-prompt", "logs", "tail", "api; echo unsafe", "--since", "30m", "--format", "detailed", "--filter-pattern", "ERROR", "--log-stream-name-prefix", "app-" }, arguments);
        source.Dispose(); source.Dispose();
        Assert.False(File.Exists(source.BackingFilePath));
    }

    [UnixFact]
    public async Task Snapshot_failure_reports_cli_diagnostic_and_removes_partial_file()
    {
        using var aws = new FakeAws(); aws.Write("stderr", "No such log group: missing\n"); aws.Write("exit", "1");
        using var host = new TempHost();
        var error = await Assert.ThrowsAsync<IOException>(async () => await new CloudWatchSourcePlugin().OpenAsync(Config("missing"), new(host)));
        Assert.Contains("No such log group", error.Message);
        Assert.Empty(Directory.GetFiles(host.Directory));
    }

    [UnixFact]
    public async Task Snapshot_cancellation_kills_the_process_and_removes_its_spool()
    {
        using var aws = new FakeAws(); aws.Write("hold", "true");
        using var host = new TempHost(); using var cancellation = new CancellationTokenSource();
        Task<ILogSource> opening = new CloudWatchSourcePlugin().OpenAsync(Config("api"), new(host, cancellation.Token)).AsTask();
        await WaitUntil(() => File.Exists(aws.PathFor("pid")));
        int pid = int.Parse(File.ReadAllText(aws.PathFor("pid")));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await opening.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(Directory.GetFiles(host.Directory));
        await WaitUntil(() => !IsRunning(pid));
    }

    [UnixFact]
    public async Task Follow_startup_failure_reports_the_diagnostic_and_releases_the_spool()
    {
        using var aws = new FakeAws(); aws.Write("stderr", "Cannot connect to AWS\n"); aws.Write("exit", "1");
        using var host = new TempHost();
        var error = await Assert.ThrowsAsync<IOException>(async () => await new CloudWatchSourcePlugin().OpenAsync(Config("api", follow: true), new(host)));
        Assert.Contains("AWS", error.Message);
        Assert.Empty(Directory.GetFiles(host.Directory));
    }

    [UnixFact]
    public async Task Follow_can_cancel_before_the_startup_grace_period_finishes()
    {
        using var aws = new FakeAws(); aws.Write("hold", "true");
        using var host = new TempHost(); using var cancellation = new CancellationTokenSource();
        Task<ILogSource> opening = new CloudWatchSourcePlugin().OpenAsync(Config("api", follow: true), new(host, cancellation.Token)).AsTask();
        await WaitUntil(() => File.Exists(aws.PathFor("pid")) && File.ReadAllText(aws.PathFor("pid")).Length > 0);
        int pid = int.Parse(File.ReadAllText(aws.PathFor("pid")));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await opening.WaitAsync(TimeSpan.FromSeconds(5)));
        await WaitUntil(() => !IsRunning(pid) && Directory.GetFiles(host.Directory).Length == 0);
    }

    [UnixFact]
    public async Task Follow_recovers_backlog_deduplicates_replay_and_stops_when_disposed()
    {
        using var aws = new FakeAws(); aws.Write("reconnect", "true");
        using var host = new TempHost(); using var opening = new CancellationTokenSource();
        using var source = await new CloudWatchSourcePlugin().OpenAsync(Config("api", follow: true), new(host, opening.Token));
        opening.Cancel(); // the source lifetime must be independent of this token
        await WaitUntil(() => Read(source.BackingFilePath).Contains("during outage"));
        string content = Read(source.BackingFilePath);
        Assert.Equal(3, content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(2, content.Split('\n').Count(line => line.EndsWith("identical")));
        Assert.Contains("--since\n2026-10-12T10:00:00.1220000+00:00", File.ReadAllText(aws.PathFor("arguments")));
        Assert.True(source.AutoReloadOnExternalChange);
        int pid = int.Parse(File.ReadAllText(aws.PathFor("pid")));
        source.Dispose();
        await WaitUntil(() => !File.Exists(source.BackingFilePath) && !IsRunning(pid));
    }


    private static PluginSourceConfiguration Config(string group, string? profile = null, string since = "1h", bool follow = false) =>
        CloudWatchConfiguration.Create(new(group,profile,"eu-central-1","app-*","ERROR",follow,since));
    private static string Read(string path) { try { return File.ReadAllText(path); } catch (IOException) { return ""; } }
    private static bool IsRunning(int pid) { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class UnixFactAttribute : FactAttribute
    {
        public UnixFactAttribute() { if (OperatingSystem.IsWindows()) Skip = "The fake CloudWatch executable uses a POSIX shell."; }
    }

    private sealed class TempHost : IPluginSourceHost, IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "aws-source-tests-" + Guid.NewGuid().ToString("N"));
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

    private sealed class FakeAws : IDisposable
    {
        private readonly string? _previousPath = Environment.GetEnvironmentVariable("PATH");
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "fake-aws-" + Guid.NewGuid().ToString("N"));
        public FakeAws()
        {
            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The fake CLI requires a POSIX shell.");
            Directory.CreateDirectory(_directory);
            string executable = PathFor("aws");
            File.WriteAllText(executable, """
                #!/bin/sh
                fixture=$(dirname "$0")
                printf '%s\n' "$@" >> "$fixture/arguments"
                printf '%s' "$$" > "$fixture/pid"
                while [ "$1" != "logs" ] && [ "$#" -gt 0 ]; do shift; done
                if [ "$2" = "describe-log-groups" ]; then
                  [ ! -f "$fixture/groups" ] || cat "$fixture/groups"
                elif [ -f "$fixture/reconnect" ]; then
                  if [ ! -f "$fixture/attempt" ]; then
                    touch "$fixture/attempt"
                    printf '%s\n' '2026-10-12T10:00:00.123+00:00 stream identical'
                  else
                    printf '%s\n' '2026-10-12T10:00:00.123+00:00 stream identical' '2026-10-12T10:00:00.123+00:00 stream identical' '2026-10-12T10:00:01.001+00:00 stream during outage'
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
