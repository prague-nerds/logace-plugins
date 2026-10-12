using LogAce.PluginApi;
using LogAce.Plugins.OsLog;
using Xunit;

public sealed class CliCaptureTests
{
    [Fact]
    public async Task Snapshot_preserves_argv_and_spool_ownership()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var fixture = new Fixture();
        var plugin = new OsLogSourcePlugin();
        string arguments = Path.Combine(fixture.DirectoryPath, "arguments.txt");
        string? old = Environment.GetEnvironmentVariable("LOGACE_ARGS");

        try
        {
            Environment.SetEnvironmentVariable("LOGACE_ARGS", arguments);
            fixture.Cli("log", @"printf '%s\n' ""$@"" > ""$LOGACE_ARGS""
printf '%s\n' 'Filtering log data' '{""eventType"":""logEvent"",""eventMessage"":""fixture""}' ");
            var configuration = Fixture.Configure(plugin, ("process", "Fixture process"));
            using var source = await plugin.OpenAsync(configuration, new(fixture));
            Assert.Contains("fixture", File.ReadAllText(source.BackingFilePath));
            string[] argv = File.ReadAllLines(arguments);
            Assert.Contains("show", argv);
            Assert.Contains("--process", argv);
            Assert.Contains("--style", argv);
            Assert.Contains("ndjson", argv);
            Assert.DoesNotContain("Filtering log data", File.ReadAllText(source.BackingFilePath));
            source.Dispose();
            source.Dispose();
            Assert.False(File.Exists(source.BackingFilePath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGACE_ARGS", old);
        }
    }

    [Fact]
    public async Task Cancelling_a_slow_CLI_kills_it_and_deletes_partial_spools()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var fixture = new Fixture();
        var plugin = new OsLogSourcePlugin();
        fixture.Cli("log", "sleep 30");
        using var token = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await plugin.OpenAsync(Fixture.Configure(plugin, ("process", "Fixture process")), new(fixture, token.Token)).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.All(fixture.Spools, path => Assert.False(File.Exists(path)));
    }
}
