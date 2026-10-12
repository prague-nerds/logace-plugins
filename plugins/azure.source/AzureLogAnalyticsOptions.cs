namespace LogAce.Plugins.Azure;

/// <summary>Version 1 persisted settings; numeric enum values and field names are compatibility contracts.</summary>
internal sealed record AzureLogAnalyticsOptions(
    string WorkspaceId,
    string Query,
    string Timespan);
