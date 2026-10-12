using LogAce.PluginApi;
using LogAce.Plugins.Gcp;
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
        var plugin = new GcpSourcePlugin();
        fixture.Cli("gcloud", @"printf '%s\n' '{""insertId"":""before""}'; sleep 1; printf '%s\n' '{""insertId"":""after-open""}'; sleep 30");
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
