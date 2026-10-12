using LogAce.Plugins.OsLog;
using Xunit;

public sealed class ProviderParsingTests
{

    [Fact]
    public void OsLogFacetCatalog_extracts_subsystem_and_process_basename()
    {
        const string line = "{\"eventType\":\"logEvent\",\"subsystem\":\"com.apple.network\",\"processImagePath\":\"/usr/sbin/bluetoothd\"}";

        Assert.True(OsLogFacetCatalog.TryExtractFacet(line, OsLogFacetKind.Subsystem, out string? subsystem));
        Assert.Equal("com.apple.network", subsystem);

        Assert.True(OsLogFacetCatalog.TryExtractFacet(line, OsLogFacetKind.Process, out string? process));
        Assert.Equal("bluetoothd", process);
    }

    [Fact]
    public void OsLogFacetCatalog_skips_missing_fields_and_non_json()
    {
        Assert.False(OsLogFacetCatalog.TryExtractFacet("{\"eventType\":\"logEvent\"}", OsLogFacetKind.Subsystem, out _));
        Assert.False(OsLogFacetCatalog.TryExtractFacet("Filtering the log data using \"info\"", OsLogFacetKind.Process, out _));
        Assert.False(OsLogFacetCatalog.TryExtractFacet(string.Empty, OsLogFacetKind.Subsystem, out _));
    }
}
