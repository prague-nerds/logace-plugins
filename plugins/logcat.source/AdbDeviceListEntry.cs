using System;

namespace LogAce.Plugins.Logcat;

/// <summary>Discovery result with stable provider ID and display metadata for an explicit selection.</summary>
internal sealed record AdbDeviceListEntry(
    string Serial,
    string State,
    string? Model,
    string? Product,
    string? TransportId)
{
    public bool IsOnline => string.Equals(State, "device", StringComparison.OrdinalIgnoreCase);
}
