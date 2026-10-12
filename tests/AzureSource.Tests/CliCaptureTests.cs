using LogAce.PluginApi;
using LogAce.Plugins.Azure;
using Xunit;

public sealed class CliCaptureTests
{
    [Fact]
    public async Task Snapshot_preserves_argv_and_spool_ownership()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new Fixture();
        var plugin = new AzureSourcePlugin();
        string arguments = Path.Combine(fixture.DirectoryPath, "arguments.txt");
        string? old = Environment.GetEnvironmentVariable("LOGACE_ARGS");

        try
        {
            Environment.SetEnvironmentVariable("LOGACE_ARGS", arguments);
            fixture.Cli("az", @"printf '%s\n' ""$@"" > ""$LOGACE_ARGS""
printf '%s\n' '{""tables"":[{""columns"":[{""name"":""Message""}],""rows"":[[""fixture""]]}]}' ");
            var configuration = Fixture.Configure(plugin, ("sourceMode", "analytics"), ("workspaceId", "workspace"), ("query", "AppTraces | take 2"));
            using var source = await plugin.OpenAsync(configuration, new(fixture));
            Assert.Contains("fixture", File.ReadAllText(source.BackingFilePath));
            string[] argv = File.ReadAllLines(arguments);
            Assert.Contains("--analytics-query", argv);
            Assert.Contains("AppTraces | take 2", argv);
            Assert.Contains("-w", argv);
            Assert.Contains("workspace", argv);
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
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new Fixture();
        var plugin = new AzureSourcePlugin();
        fixture.Cli("az", "sleep 30");
        using var token = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await plugin.OpenAsync(Fixture.Configure(plugin, ("sourceMode", "analytics"), ("workspaceId", "workspace"), ("query", "AppTraces | take 2")), new(fixture, token.Token)).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.All(fixture.Spools, path => Assert.False(File.Exists(path)));
    }
}
