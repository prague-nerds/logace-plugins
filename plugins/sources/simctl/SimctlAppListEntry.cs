namespace LogAce.Plugins.Simctl;

/// <summary>Discovery result with stable provider ID and display metadata for an explicit selection.</summary>
internal sealed record SimctlAppListEntry(
    string BundleId,
    string DisplayName,
    string Executable,
    string ApplicationType);
