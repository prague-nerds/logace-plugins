using LogAce.PluginApi;
namespace LogAce.Columns.user_agent;
internal static class ColumnDataPaths
{
    private static string? _data;
    private static string? _legacy;
    public static void Initialize(PluginHostServices services)
    {
        _data = services.DataDirectory;
        _legacy = services.LegacyDataDirectory;
    }
    public static string BotDatabaseDirectoryPath
    {
        get
        {
            string target = _data ?? throw new InvalidOperationException("Plugin data directory is unavailable.");
            Directory.CreateDirectory(target);
            foreach (string file in new string[] {"user-agents.json" })
            {
                string destination = Path.Combine(target, file);
                string? previous = _legacy is null ? null : Path.Combine(_legacy, "bots", file);
                if (!File.Exists(destination) && previous is not null && File.Exists(previous))
                {
                    try { File.Copy(previous, destination, false); }
                    catch (IOException) when (File.Exists(destination)) { }
                }
            }
            return target;
        }
    }
}
