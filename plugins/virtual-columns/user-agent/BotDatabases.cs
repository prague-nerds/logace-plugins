using LogAce.PluginApi;
namespace LogAce.Columns.user_agent;
internal static class BotDatabases
{
    public static BotDatabaseStore<BotUserAgentDatabase> UserAgents { get; } = new(
        Path.Combine(ColumnDataPaths.BotDatabaseDirectoryPath, "user-agents.json"),
        BotUserAgentDatabase.SourceUrl,
        BotUserAgentDatabase.Parse,
        static database => database.Count);

}
