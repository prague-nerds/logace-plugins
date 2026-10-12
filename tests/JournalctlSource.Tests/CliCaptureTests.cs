using LogAce.PluginApi;
using LogAce.Plugins.Journalctl;
using Xunit;

public sealed class CliCaptureTests
{
    [Fact]
    public async Task Snapshot_preserves_argv_and_spool_ownership()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = new Fixture();
        var plugin = new JournalctlSourcePlugin();
        string arguments = Path.Combine(fixture.DirectoryPath, "arguments.txt");
        string? old = Environment.GetEnvironmentVariable("LOGACE_ARGS");

        try
        {
            Environment.SetEnvironmentVariable("LOGACE_ARGS", arguments);
            fixture.Cli("journalctl", @"printf '%s\n' ""$@"" > ""$LOGACE_ARGS""
printf '%s\n' '{""MESSAGE"":""fixture"",""__REALTIME_TIMESTAMP"":""1""}' ");
            var configuration = Fixture.Configure(plugin, ("follow", false), ("unit", "fixture.service"), ("since", "boot"));
            using var source = await plugin.OpenAsync(configuration, new(fixture));
            Assert.Contains("fixture", File.ReadAllText(source.BackingFilePath));
            string[] argv = File.ReadAllLines(arguments);
            Assert.Contains("--unit", argv);
            Assert.Contains("fixture.service", argv);
            Assert.Contains("-b", argv);
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
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = new Fixture();
        var plugin = new JournalctlSourcePlugin();
        fixture.Cli("journalctl", "sleep 30");
        using var token = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await plugin.OpenAsync(Fixture.Configure(plugin, ("follow", false), ("unit", "fixture.service"), ("since", "boot")), new(fixture, token.Token)).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.All(fixture.Spools, path => Assert.False(File.Exists(path)));
    }
}
