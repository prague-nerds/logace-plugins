namespace LogAce.Plugins.Azure;

/// <summary>Version 1 persisted settings; numeric enum values and field names are compatibility contracts.</summary>
internal sealed record AzureAppServiceOptions(
    string? Subscription,
    string ResourceGroup,
    string AppName,
    string? Slot);
