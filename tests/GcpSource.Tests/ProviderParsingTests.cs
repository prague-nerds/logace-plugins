using LogAce.Plugins.Gcp;
using Xunit;

public sealed class ProviderParsingTests
{

    [Fact]
    public void Gcp_json_array_normalizes_to_ndjson()
    {
        const string json = """
        [
          { "insertId": "1", "severity": "ERROR" },
          { "insertId": "2", "severity": "INFO" }
        ]
        """;

        Assert.True(GcpLoggingSourceFactory.TryNormalizeJsonArrayAsNdjson(json, out string ndjson, out int count, out string? error), error);
        Assert.Equal(2, count);
        string[] lines = ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"insertId\"", lines[0]);
        Assert.Contains("\"1\"", lines[0]);
        Assert.Contains("\"insertId\"", lines[1]);
        Assert.Contains("\"2\"", lines[1]);
    }
}
