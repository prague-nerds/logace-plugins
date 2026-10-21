using LogAce.PluginApi;
namespace LogAce.Columns.bots_ip;
internal static class BotDatabases
{
    public static BotDatabaseStore<BotIpDatabase> IpRanges { get; } = new(
        Path.Combine(ColumnDataPaths.BotDatabaseDirectoryPath, "crawler-ips.json"),
        BotIpDatabase.SourceUrl,
        BotIpDatabase.Parse,
        static database => database.Count);

    public static BotDatabaseStore<BotIpDatabase> Datacenters { get; } = new(
        Path.Combine(ColumnDataPaths.BotDatabaseDirectoryPath, "datacenters.csv"),
        BotIpDatabase.DatacenterSourceUrl,
        BotIpDatabase.ParseDatacenters,
        static database => database.Count);
}
