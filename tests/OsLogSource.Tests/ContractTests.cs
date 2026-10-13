using LogAce.PluginApi;
using LogAce.Plugins.OsLog;
using Xunit;

public sealed class ContractTests
{
    [Fact]
    public async Task CLI_drains_large_stderr_without_blocking_and_retains_bounded_diagnostics()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new Fixture();
        var result = await SourceRuntime.DiscoverAsync(default, () =>
        {
            using var process = SourceRuntime.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                ArgumentList = { "-c", "head -c 131072 /dev/zero | tr '\\000' E >&2; printf success" }
            })!;
            return SourceRuntime.ReadResult(process);
        }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("success", result.Output);
        Assert.Equal(16384, result.Error.Length);
    }

    [Fact]
    public async Task Precancelled_open_does_not_allocate_a_spool()
    {
        using var fixture = new Fixture();
        var plugin = new OsLogSourcePlugin();
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
