namespace LogAce.Plugins.Simctl;

/// <summary>Discovery result with stable provider ID and display metadata for an explicit selection.</summary>
internal sealed record SimctlProcessListEntry(
    int Pid,
    string Name,
    string Label);
