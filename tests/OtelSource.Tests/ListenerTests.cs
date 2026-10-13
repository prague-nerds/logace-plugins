using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using LogAce.PluginApi;
using LogAce.Plugins.Otel;
using Xunit;

public sealed class ListenerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Listener_accepts_JSON_protobuf_and_gzip_and_owns_its_port(bool protobuf, bool gzip)
    {
        using var fixture = new Fixture();
        var plugin = new OtelSourcePlugin();
        int port = GetFreeLoopbackPort();
        using var opening = new CancellationTokenSource();
        using var source = await plugin.OpenAsync(Fixture.Configure(plugin, ("bindAddress", "127.0.0.1"), ("port", port)), new(fixture, opening.Token));
        opening.Cancel(); // Startup cancellation no longer owns a successfully opened live source.
        byte[] payload = protobuf ? BuildOtelProtobufPayload() : Encoding.UTF8.GetBytes(CreateSingleLogJson());

        if (gzip)
        {
            using var memory = new MemoryStream();

            using (var compressed = new GZipStream(memory, CompressionMode.Compress, true))
            {
                compressed.Write(payload);
            }

            payload = memory.ToArray();
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new(protobuf ? "application/x-protobuf" : "application/json");

        if (gzip)
        {
            content.Headers.ContentEncoding.Add("gzip");
        }

        using var response = await client.PostAsync($"http://127.0.0.1:{port}/v1/logs", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string line = (await Fixture.WaitForAsync(source.BackingFilePath, "payment failed")).Trim();
        using var document = JsonDocument.Parse(line);
        Assert.Equal("checkout-api", document.RootElement.GetProperty("serviceName").GetString());
        Assert.Equal("ERROR", document.RootElement.GetProperty("severity").GetString());
        source.Dispose();
        source.Dispose();
        Assert.False(File.Exists(source.BackingFilePath));
        using var reopened = await plugin.OpenAsync(Fixture.Configure(plugin, ("bindAddress", "127.0.0.1"), ("port", port)), new(fixture));
    }

    [Fact]
    public async Task Listener_rejects_wrong_route_method_and_invalid_payload()
    {
        using var fixture = new Fixture();
        var plugin = new OtelSourcePlugin();
        int port = GetFreeLoopbackPort();
        using var source = await plugin.OpenAsync(Fixture.Configure(plugin, ("bindAddress", "127.0.0.1"), ("port", port)), new(fixture));
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        string endpoint = $"http://127.0.0.1:{port}/v1/logs";
        using var get = await client.GetAsync(endpoint);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        using var missing = await client.PostAsync(endpoint + "-wrong", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var invalid = await client.PostAsync(endpoint, new StringContent("{bad", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Empty(File.ReadAllText(source.BackingFilePath));
    }

    [Fact]
    public async Task Listener_bind_failure_leaves_no_spool()
    {
        using var occupied = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        int port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        using var fixture = new Fixture();
        var plugin = new OtelSourcePlugin();
        await Assert.ThrowsAsync<IOException>(async () => await plugin.OpenAsync(Fixture.Configure(plugin, ("bindAddress", "127.0.0.1"), ("port", port)), new(fixture)));
        Assert.All(fixture.Spools, path => Assert.False(File.Exists(path)));
    }

    [Theory]
    [InlineData("0.0.0.0", 4318, "/v1/logs", "http://127.0.0.1:4318/v1/logs")]
    [InlineData("::", 4318, "custom/logs", "http://[::1]:4318/custom/logs")]
    [InlineData("192.0.2.10", 9000, "/logs", "http://192.0.2.10:9000/logs")]
    public void Exporter_hint_uses_a_connectable_address(string address, int port, string path, string expected)
    {
        var plugin = new OtelSourcePlugin();
        var config = Fixture.Configure(plugin, ("bindAddress", address), ("port", port), ("path", path));
        var form = (PluginSourceFormRequest)plugin.GetConfigurationUi(new(config));
        Assert.Contains(expected, form.Form.Describe!(new(config.Values.EnumerateObject().Select(p => new KeyValuePair<string, JsonElement>(p.Name, p.Value)))));
        var invalid = Fixture.Configure(plugin, ("bindAddress", "bad host"), ("port", 70000));
        Assert.DoesNotContain("http://", OtelSourcePlugin.ExporterHint(new(invalid.Values.EnumerateObject().Select(p => new KeyValuePair<string, JsonElement>(p.Name, p.Value)))));
    }

    private static byte[] BuildOtelProtobufPayload()
    {
        byte[] serviceName = KeyValue("service.name", AnyString("checkout-api"));
        byte[] resource = Message((1, serviceName));
        byte[] scope = Message(
            (1, StringValue("Checkout.Logger")),
            (2, StringValue("1.2.3")));
        byte[] logRecord = Message(
            Fixed64Field(1, 1719316800123456789UL),
            VarintField(2, 17),
            (3, StringValue("ERROR")),
            (5, AnyString("payment failed")),
            (6, KeyValue("order.id", AnyInt(42))),
            (9, Convert.FromHexString("0af7651916cd43dd8448eb211c80319c")),
            (10, Convert.FromHexString("b7ad6b7169203331")),
            Fixed64Field(11, 1719316801123456789UL),
            (12, StringValue("payment.failed")));
        byte[] scopeLogs = Message((1, scope), (2, logRecord));
        byte[] resourceLogs = Message((1, resource), (2, scopeLogs));
        return Message((1, resourceLogs));
    }

    private static string CreateSingleLogJson()
    {
        return """
        {
          "resourceLogs": [
            {
              "resource": {
                "attributes": [
                  { "key": "service.name", "value": { "stringValue": "checkout-api" } }
                ]
              },
              "scopeLogs": [
                {
                  "scope": { "name": "Checkout.Logger" },
                  "logRecords": [
                    {
                      "timeUnixNano": "1719316800123456789",
                      "severityNumber": 17,
                      "severityText": "ERROR",
                      "body": { "stringValue": "payment failed" }
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static byte[] AnyString(string value)
    {
        return Message((1, StringValue(value)));
    }

    private static byte[] AnyInt(long value)
    {
        return Message(VarintField(3, unchecked((ulong)value)));
    }

    private static byte[] KeyValue(string key, byte[] anyValue)
    {
        return Message((1, StringValue(key)), (2, anyValue));
    }

    private static (int Field, byte[] Payload) Fixed64Field(int field, ulong value)
    {
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, value);
        return (field | (1 << 29), payload);
    }

    private static (int Field, byte[] Payload) VarintField(int field, ulong value)
    {
        return (field | (2 << 29), WriteVarint(value));
    }

    private static byte[] StringValue(string value)
    {
        return Encoding.UTF8.GetBytes(value);
    }

    private static byte[] Message(params (int Field, byte[] Payload)[] fields)
    {
        using var stream = new MemoryStream();
        foreach ((int encodedField, byte[] payload) in fields)
        {
            int fieldKind = encodedField >> 29;
            int field = encodedField & 0x1FFFFFFF;
            int wireType = fieldKind switch
            {
                1 => 1,
                2 => 0,
                _ => 2
            };

            stream.Write(WriteVarint(((ulong)field << 3) | (uint)wireType));

            if (wireType == 2)
            {
                stream.Write(WriteVarint((ulong)payload.Length));
            }

            stream.Write(payload);
        }

        return stream.ToArray();
    }

    private static byte[] WriteVarint(ulong value)
    {
        var bytes = new List<byte>();

        while (value >= 0x80)
        {
            bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }

        bytes.Add((byte)value);
        return bytes.ToArray();
    }
}
