namespace LogAce.Plugins.OsLog;

/// <summary>Version 1 persisted settings; numeric enum values and field names are compatibility contracts.</summary>
internal sealed record OsLogSourceOptions(
    string? Predicate,
    string? Process,
    string? Subsystem,
    OsLogLevel MinimumLevel,
    OsLogOpenMode OpenMode,
    string Last);
