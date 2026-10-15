namespace LogAce.Plugins.Journalctl;

/// <summary>Version 1 persisted settings; numeric enum values and field names are compatibility contracts.</summary>
internal sealed record JournalctlSourceOptions(
    string? Unit,
    string Tail,
    string? Since,
    bool Follow);
