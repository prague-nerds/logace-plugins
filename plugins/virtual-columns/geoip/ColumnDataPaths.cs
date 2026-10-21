using LogAce.PluginApi;
namespace LogAce.Columns.geoip;
internal static class ColumnDataPaths
{
    private static string? _data;
    private static string? _legacy;
    public static void Initialize(PluginHostServices services)
    {
        _data = services.DataDirectory;
        _legacy = services.LegacyDataDirectory;
    }
    public static string? NormalizeConfiguredPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string candidate = path.Trim();
        if (candidate == "~")
        {
            candidate = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        else if (candidate.StartsWith("~/", StringComparison.Ordinal)
            || candidate.StartsWith("~\\", StringComparison.Ordinal))
        {
            candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), candidate[2..]);
        }

        try
        {
            return Path.GetFullPath(candidate);
        }
        catch
        {
            return candidate;
        }
    }

    public static string? ResolveGeoIpCityDatabaseReadPath(string? configured) => Resolve("LOGACE_GEOIP_CITY_DB_PATH", configured, "GeoLite2-City.mmdb");
    public static string? ResolveGeoIpAsnDatabaseReadPath(string? configured) => Resolve("LOGACE_GEOIP_ASN_DB_PATH", configured, "GeoLite2-ASN.mmdb");
    private static string? Resolve(string environment, string? configured, string file)
    {
        foreach (string? candidate in new[] { Environment.GetEnvironmentVariable(environment), configured, _legacy is null ? null : Path.Combine(_legacy, "geoip", file) })
        {
            string? path = NormalizeConfiguredPath(candidate);
            if (path is not null && File.Exists(path)) return path;
        }
        return null;
    }
}
