namespace LogAce.Plugins.Simctl;

/// <summary>Version 1 persisted settings; numeric enum values and field names are compatibility contracts.</summary>
internal sealed record SimctlLogSourceOptions(
    string DeviceSelector,
    string? DeviceUdid,
    string? DeviceName,
    string? RuntimeDisplayName,
    string? AppBundleId,
    string? AppDisplayName,
    string? AppExecutable,
    string? Process,
    int? ProcessId,
    string Last,
    bool IncludeInfo,
    bool IncludeDebug);
