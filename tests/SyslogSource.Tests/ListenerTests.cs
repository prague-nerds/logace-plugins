using System.Net;
using System.Net.Sockets;
using System.Text;
using LogAce.PluginApi;
using LogAce.Plugins.Syslog;
using Xunit;

public sealed class ListenerTests
{
    [Fact]
    public async Task UDP_accepts_RFC5424_and_preserves_header_and_transport_metadata()
    {
        using var fixture = new Fixture();
        var plugin = new SyslogSourcePlugin();
        int port = FreeUdpPort();
        using var opening = new CancellationTokenSource();
        using var source = await plugin.OpenAsync(Fixture.Configure(plugin, ("bindAddress", "127.0.0.1"), ("udpPort", port), ("tcpPort", null)), new(fixture, opening.Token));
        opening.Cancel();
        using var client = new UdpClient();
        await client.SendAsync(Encoding.UTF8.GetBytes("<134>1 2026-10-12T10:00:00Z host app 42 msg [example@32473 key=\"value\"] fixture message"), new IPEndPoint(IPAddress.Loopback, port));
        string data = await Fixture.WaitForAsync(source.BackingFilePath, "fixture message");
        Assert.StartsWith("#syslog-sink-v2\tTime\tHost", data);
        Assert.Contains("\tudp\t127.0.0.1\t" + port + "\t", data);
        Assert.Contains("host\tapp\t42\tmsg\t134", data);
        source.Dispose();
        source.Dispose();
        Assert.False(File.Exists(source.BackingFilePath));
        using var reopened = await plugin.OpenAsync(Fixture.Configure(plugin, ("bindAddress", "127.0.0.1"), ("udpPort", port), ("tcpPort", null)), new(fixture));
    }

    [Fact]
    public async Task TCP_accepts_split_octet_counted_UTF8_and_newline_frames()
    {
        using var fixture = new Fixture();
        var plugin = new SyslogSourcePlugin();
        int port = FreeTcpPort();
        using var source = await plugin.OpenAsync(Fixture.Configure(plugin, ("bindAddress", "127.0.0.1"), ("udpPort", null), ("tcpPort", port)), new(fixture));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        byte[] message = Encoding.UTF8.GetBytes("<13>Oct 12 10:00:00 host app: žluťoučký fixture");
        byte[] frame = Encoding.ASCII.GetBytes(message.Length + " ").Concat(message).ToArray();
        await stream.WriteAsync(frame.AsMemory(0, 3));
        await stream.WriteAsync(frame.AsMemory(3));
        await stream.WriteAsync(Encoding.UTF8.GetBytes("<13>Oct 12 10:00:01 host app: second fixture\n"));
        string data = await Fixture.WaitForAsync(source.BackingFilePath, "second fixture");
        Assert.Contains("žluťoučký fixture", data);
        Assert.Contains("\ttcp\t127.0.0.1\t", data);
        Assert.Equal(3, data.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        source.Dispose();
        Assert.False(File.Exists(source.BackingFilePath));
    }

    [Fact]
    public async Task Partial_bind_failure_releases_the_first_transport_and_spool()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        int tcp = ((IPEndPoint)occupied.LocalEndpoint).Port;
        int udp = FreeUdpPort();
        using var fixture = new Fixture();
        var plugin = new SyslogSourcePlugin();
        await Assert.ThrowsAsync<IOException>(async () => await plugin.OpenAsync(Fixture.Configure(plugin, ("bindAddress", "127.0.0.1"), ("udpPort", udp), ("tcpPort", tcp)), new(fixture)));
        Assert.All(fixture.Spools, path => Assert.False(File.Exists(path)));
        using var check = new UdpClient(new IPEndPoint(IPAddress.Loopback, udp));
    }

    [Fact]
    public void Omitted_nullable_port_stays_disabled_in_legacy_configuration()
    {
        var plugin = new SyslogSourcePlugin();
        var saved = new PluginSourceConfiguration(1, System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            bindAddress = "127.0.0.1",
            tcpPort = 5514
        }));
        var normalized = plugin.NormalizeConfiguration(saved);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, normalized.Values.GetProperty("udpPort").ValueKind);
        Assert.True(plugin.ValidateConfiguration(normalized).IsValid);
        Assert.False(plugin.ValidateConfiguration(Fixture.Configure(plugin, ("tcpPort", null), ("udpPort", null))).IsValid);
    }

    private static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static int FreeUdpPort()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
    }
}
