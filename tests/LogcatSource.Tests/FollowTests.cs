using LogAce.PluginApi;
using LogAce.Plugins.Logcat;
using Xunit;

public sealed class FollowTests
{
    [Fact]
    public async Task Successful_follow_survives_old_opening_token_and_disposes_its_spool()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new Fixture();
        var plugin = new LogcatSourcePlugin();
        fixture.Cli("adb", @"case ""$*"" in ""devices -l"") printf 'List of devices attached\nserial device model:Phone\n';; *) printf '2026-10-13 10:00:00.123  1  2 I T: before\n'; sleep 1; printf '2026-10-13 10:00:01.123  1  2 I T: after-open\n'; sleep 30;; esac");
        using var opening = new CancellationTokenSource();
        using var source = await plugin.OpenAsync(Fixture.Configure(plugin, ("openMode", 1)), new(fixture, opening.Token)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        opening.Cancel();
        Assert.Contains("after-open", await Fixture.WaitForAsync(source.BackingFilePath, "after-open"));
        Assert.True(source.AutoReloadOnExternalChange);
        Assert.False(source.IsFileSystemSource);
        source.Dispose();
        source.Dispose();
        Assert.False(File.Exists(source.BackingFilePath));
    }
}
