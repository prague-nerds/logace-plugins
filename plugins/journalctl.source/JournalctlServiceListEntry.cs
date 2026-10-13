namespace LogAce.Plugins.Journalctl;

/// <summary>Discovery result with stable provider ID and display metadata for an explicit selection.</summary>
internal sealed record JournalctlServiceListEntry(
    string? Unit,
    string Load,
    string Active,
    string Sub,
    string Description);
