namespace LogAce.Plugins.Logcat;

/// <summary>Provider-specific helpers shared by discovery, capture and identity handling within this package.</summary>
internal static class LogcatOptionTokens
{
    public const string AnyDeviceSelector = "any";

    public static string ToBufferArgument(LogcatBuffer buffer)
    {
        return buffer switch
        {
            LogcatBuffer.System => "system",
            LogcatBuffer.Crash => "crash",
            LogcatBuffer.Radio => "radio",
            LogcatBuffer.Events => "events",
            LogcatBuffer.Kernel => "kernel",
            LogcatBuffer.All => "all",
            _ => "main"
        };
    }

    public static bool TryParseBuffer(string value, out LogcatBuffer buffer)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "main":
                buffer = LogcatBuffer.Main;
                return true;
            case "system":
                buffer = LogcatBuffer.System;
                return true;
            case "crash":
                buffer = LogcatBuffer.Crash;
                return true;
            case "radio":
                buffer = LogcatBuffer.Radio;
                return true;
            case "events":
                buffer = LogcatBuffer.Events;
                return true;
            case "kernel":
                buffer = LogcatBuffer.Kernel;
                return true;
            case "all":
                buffer = LogcatBuffer.All;
                return true;
            default:
                buffer = LogcatBuffer.Main;
                return false;
        }
    }

    public static char ToLevelLetter(LogcatLevel level)
    {
        return level switch
        {
            LogcatLevel.Debug => 'D',
            LogcatLevel.Info => 'I',
            LogcatLevel.Warn => 'W',
            LogcatLevel.Error => 'E',
            LogcatLevel.Fatal => 'F',
            _ => 'V'
        };
    }

    public static LogcatLevel ParseLevel(string? value)
    {
        return value?.Trim().ToUpperInvariant() switch
        {
            "D" or "DEBUG" => LogcatLevel.Debug,
            "I" or "INFO" => LogcatLevel.Info,
            "W" or "WARN" or "WARNING" => LogcatLevel.Warn,
            "E" or "ERROR" => LogcatLevel.Error,
            "F" or "FATAL" => LogcatLevel.Fatal,
            _ => LogcatLevel.Verbose
        };
    }
}
