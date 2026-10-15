namespace LogAce.Plugins.Syslog;

/// <summary>Version 1 persisted settings; numeric enum values and field names are compatibility contracts.</summary>
internal sealed record SyslogSinkSourceOptions(
    string BindAddress,
    int? UdpPort,
    int? TcpPort)
{
    public bool UseUdp => UdpPort is not null;

    public bool UseTcp => TcpPort is not null;
}
