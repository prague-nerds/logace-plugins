using LogAce.PluginApi;
using LogAce.Plugins.Otel;
using Xunit;

public sealed class ContractTests
{
    [Fact]
    public async Task Precancelled_open_does_not_allocate_a_spool()
    {
        using var fixture = new Fixture();
        var plugin = new OtelSourcePlugin();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var configuration = plugin.CreateDefaultConfiguration();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await plugin.OpenAsync(configuration, new(fixture, cancellation.Token)));
        Assert.Empty(fixture.Spools);
    }

    [Fact]
    public async Task Failed_or_cancelled_open_cleans_every_host_allocated_spool()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<IOException>(async () => await SourceRuntime.OpenAsync(new(fixture), () =>
        {
            File.WriteAllText(SourceRuntime.CreateSpool(), "partial");
            throw new IOException("fixture failure");
        }));
        Assert.All(fixture.Spools, path => Assert.False(File.Exists(path)));
    }
}
