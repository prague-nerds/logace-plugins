using System.Collections.Generic;

namespace LogAce.Plugins.Logcat;

/// <summary>Version 1 persisted settings; numeric enum values and field names are compatibility contracts.</summary>
internal sealed record LogcatSourceOptions(
    string DeviceSelector,
    string? DeviceSerial,
    string? DeviceModel,
    IReadOnlyList<LogcatBuffer> Buffers,
    LogcatLevel MinimumLevel,
    IReadOnlyList<string> Tags,
    string? Package,
    int? ProcessId,
    LogcatOpenMode OpenMode,
    string Tail,
    bool ClearBufferFirst);
