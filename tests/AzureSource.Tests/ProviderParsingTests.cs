using LogAce.Plugins.Azure;
using Xunit;

public sealed class ProviderParsingTests
{

    [Fact]
    public void Azure_loganalytics_table_normalizes_rows_to_ndjson()
    {
        const string json = """
        {
          "tables": [
            {
              "name": "PrimaryResult",
              "columns": [
                { "name": "TimeGenerated", "type": "datetime" },
                { "name": "Message", "type": "string" }
              ],
              "rows": [
                [ "2026-06-16T12:00:00Z", "failed" ],
                [ "2026-06-16T12:01:00Z", "ok" ]
              ]
            }
          ]
        }
        """;

        Assert.True(AzureLogAnalyticsSourceFactory.TryNormalizeLogAnalyticsJsonToNdjson(json, out string ndjson, out int count, out string? error), error);
        Assert.Equal(2, count);
        string[] lines = ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("\"TimeGenerated\":\"2026-06-16T12:00:00Z\"", lines[0]);
        Assert.Contains("\"Message\":\"failed\"", lines[0]);
        Assert.Contains("\"Message\":\"ok\"", lines[1]);
    }
}
