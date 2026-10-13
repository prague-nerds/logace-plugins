namespace LogAce.Plugins.Otel;

/// <summary>Version 1 persisted settings; numeric enum values and field names are compatibility contracts.</summary>
internal sealed record OtelLogSourceOptions(
    string BindAddress,
    int Port,
    string Path);
