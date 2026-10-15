using System;

namespace LogAce.Plugins.Simctl;

/// <summary>Discovery result with stable provider ID and display metadata for an explicit selection.</summary>
internal sealed record SimctlDeviceListEntry(
    string Udid,
    string Name,
    string RuntimeDisplayName,
    string State)
{
    public bool IsBooted => string.Equals(State, "Booted", StringComparison.OrdinalIgnoreCase);
}
