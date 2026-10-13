using LogAce.PluginApi;
using LogAce.Plugins.Logcat;
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
        var plugin = new LogcatSourcePlugin();
        string arguments = Path.Combine(fixture.DirectoryPath, "arguments.txt");
        string? old = Environment.GetEnvironmentVariable("LOGACE_ARGS");

        try
        {
            Environment.SetEnvironmentVariable("LOGACE_ARGS", arguments);
            fixture.Cli("adb", @"case ""$*"" in
  ""devices -l"") printf 'List of devices attached\nfixture-serial device model:Fixture_Phone\n' ;;
  *) printf '%s\n' ""$@"" > ""$LOGACE_ARGS""; printf '2026-10-12 10:00:00.123  1  2 I Fixture: fixture\n' ;;
esac");
            var configuration = Fixture.Configure(plugin);
            using var source = await plugin.OpenAsync(configuration, new(fixture));
            Assert.Contains("fixture", File.ReadAllText(source.BackingFilePath));
            string[] argv = File.ReadAllLines(arguments);
            Assert.Contains("-s", argv);
            Assert.Contains("fixture-serial", argv);
            Assert.Contains("logcat", argv);
            Assert.Contains("-d", argv);
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
        var plugin = new LogcatSourcePlugin();
        fixture.Cli("adb", "sleep 30");
        using var token = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await plugin.OpenAsync(Fixture.Configure(plugin), new(fixture, token.Token)).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.All(fixture.Spools, path => Assert.False(File.Exists(path)));
    }
}
