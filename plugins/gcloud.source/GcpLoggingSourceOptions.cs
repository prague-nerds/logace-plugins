namespace LogAce.Plugins.Gcp;

/// <summary>Version 1 persisted settings; numeric enum values and field names are compatibility contracts.</summary>
internal sealed record GcpLoggingSourceOptions(
    string? Project,
    string? Filter,
    GcpLoggingOpenMode OpenMode,
    string Freshness,
    int Limit);
