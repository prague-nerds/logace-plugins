using LogAce.PluginApi;
namespace LogAce.Columns.bots_ip;
using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

internal sealed partial class BotIpDatabase
{
    public const string SourceUrl = "https://raw.githubusercontent.com/hexydec/ip-ranges/main/output/crawlers.json";
    public const string DatacenterSourceUrl = "https://raw.githubusercontent.com/hexydec/ip-ranges/main/output/datacentres.csv";

    private readonly PrefixBucket[] _ipv4;
    private readonly PrefixBucket[] _ipv6;
    private VerificationRule[] _verificationRules = [];

    private BotIpDatabase(PrefixBucket[] ipv4, PrefixBucket[] ipv6)
    {
        _ipv4 = ipv4;
        _ipv6 = ipv6;
        Count = ipv4.Sum(static bucket => bucket.Names.Count) + ipv6.Sum(static bucket => bucket.Names.Count);
    }

    // Duplicate CIDRs are merged, so this is the number of distinct normalized ranges.
    public int Count { get; }

    public static BotIpDatabase Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("The bot IP database is empty.");
        }

        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
        {
            throw new InvalidDataException("The bot IP database must contain a nonempty array of crawler ranges.");
        }

        var ipv4 = new Dictionary<int, Dictionary<UInt128, SortedSet<string>>>();
        var ipv6 = new Dictionary<int, Dictionary<UInt128, SortedSet<string>>>();
        var verification = new Dictionary<string, VerificationRangeBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !TryReadRequiredString(entry, "name", out string name)
                || !TryReadRequiredString(entry, "range", out string range)
                || !TryParseRange(range, out UInt128 network, out int prefix, out bool isIpv4))
            {
                throw new InvalidDataException("The bot IP database contains an invalid crawler name or IP range.");
            }

            AddRange(isIpv4 ? ipv4 : ipv6, network, prefix, name);
            if (VerificationTokens.ContainsKey(name))
            {
                if (!verification.TryGetValue(name, out VerificationRangeBuilder? builder))
                    verification.Add(name, builder = new VerificationRangeBuilder());
                AddRange(isIpv4 ? builder.Ipv4 : builder.Ipv6, network, prefix, name);
            }
        }

        return new BotIpDatabase(Freeze(ipv4, 32), Freeze(ipv6, 128))
        {
            _verificationRules = verification.Select(pair => new VerificationRule(
                VerificationTokens[pair.Key],
                new BotIpDatabase(Freeze(pair.Value.Ipv4, 32), Freeze(pair.Value.Ipv6, 128)))).ToArray()
        };
    }

    public static BotIpDatabase ParseDatacenters(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            throw new InvalidDataException("The datacenter IP database is empty.");
        }

        var ipv4 = new Dictionary<int, Dictionary<UInt128, SortedSet<string>>>();
        var ipv6 = new Dictionary<int, Dictionary<UInt128, SortedSet<string>>>();
        using var parser = new TextFieldParser(new StringReader(csv.TrimStart('\uFEFF')))
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = true
        };
        parser.SetDelimiters(",");

        try
        {
            while (!parser.EndOfData)
            {
                // The published CSV has no header: provider, CIDR/address, optional country.
                string[]? fields = parser.ReadFields();
                if (fields is null || fields.Length is < 2 or > 3
                    || string.IsNullOrWhiteSpace(fields[0])
                    || !TryParseRange(fields[1], out UInt128 network, out int prefix, out bool isIpv4))
                {
                    throw new InvalidDataException("The datacenter IP database contains an invalid provider name or IP range.");
                }

                AddRange(isIpv4 ? ipv4 : ipv6, network, prefix, fields[0].Trim());
            }
        }
        catch (MalformedLineException exception)
        {
            throw new InvalidDataException("The datacenter IP database contains invalid CSV quoting.", exception);
        }

        if (ipv4.Count == 0 && ipv6.Count == 0)
        {
            throw new InvalidDataException("The datacenter IP database is empty.");
        }

        return new BotIpDatabase(Freeze(ipv4, 32), Freeze(ipv6, 128));
    }

    public string? Match(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return Match(_ipv4, ToNumber(address));
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            // Mapped ranges of /96 or narrower are normalized to IPv4 while parsing.
            // Any remaining IPv6 range is therefore less specific than an IPv4 match.
            string? mappedMatch = Match(_ipv4, ToNumber(address.MapToIPv4()));
            if (mappedMatch is not null)
            {
                return mappedMatch;
            }
        }

        return Match(_ipv6, ToNumber(address));
    }

    private static string? Match(PrefixBucket[] buckets, UInt128 address)
    {
        // At most 33 IPv4 or 129 IPv6 dictionary probes, regardless of database size.
        foreach (PrefixBucket bucket in buckets)
        {
            if (bucket.Names.TryGetValue(address & bucket.Mask, out string? name))
            {
                return name;
            }
        }

        return null;
    }

    private static PrefixBucket[] Freeze(Dictionary<int, Dictionary<UInt128, SortedSet<string>>> ranges, int bits)
    {
        return ranges
            .OrderByDescending(static item => item.Key)
            .Select(item => new PrefixBucket(
                Mask(bits, item.Key),
                item.Value.ToFrozenDictionary(static network => network.Key, static network => string.Join(", ", network.Value))))
            .ToArray();
    }

    private static void AddRange(Dictionary<int, Dictionary<UInt128, SortedSet<string>>> family, UInt128 network, int prefix, string name)
    {
        if (!family.TryGetValue(prefix, out Dictionary<UInt128, SortedSet<string>>? networks))
        {
            networks = new Dictionary<UInt128, SortedSet<string>>();
            family.Add(prefix, networks);
        }

        if (!networks.TryGetValue(network, out SortedSet<string>? names))
        {
            names = new SortedSet<string>(StringComparer.Ordinal);
            networks.Add(network, names);
        }

        names.Add(name);
    }

    private static bool TryReadRequiredString(JsonElement entry, string propertyName, out string value)
    {
        value = string.Empty;
        if (entry.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString()?.Trim() ?? string.Empty;
        }

        return value.Length > 0;
    }

    private static bool TryParseRange(string range, out UInt128 network, out int prefix, out bool isIpv4)
    {
        network = 0;
        prefix = 0;
        isIpv4 = false;
        int separator = range.IndexOf('/');
        ReadOnlySpan<char> addressText = separator < 0 ? range.AsSpan() : range.AsSpan(0, separator);
        if (addressText.Contains('%') || !IPAddress.TryParse(addressText, out IPAddress? address))
        {
            return false;
        }

        isIpv4 = address.AddressFamily == AddressFamily.InterNetwork;
        int bits = isIpv4 ? 32 : 128;
        prefix = bits;
        if (separator >= 0
            && (!int.TryParse(range.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out prefix)
                || prefix > bits))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6 && prefix >= 96)
        {
            address = address.MapToIPv4();
            prefix -= 96;
            bits = 32;
            isIpv4 = true;
        }

        // Upstream contains both bare addresses and CIDRs with nonzero host bits.
        network = ToNumber(address) & Mask(bits, prefix);
        return true;
    }

    private static UInt128 Mask(int bits, int prefix)
    {
        return prefix == 0 ? 0 : UInt128.MaxValue << (bits - prefix);
    }

    private static UInt128 ToNumber(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out int written);
        return written == 4
            ? BinaryPrimitives.ReadUInt32BigEndian(bytes)
            : BinaryPrimitives.ReadUInt128BigEndian(bytes);
    }

    private sealed record PrefixBucket(UInt128 Mask, FrozenDictionary<UInt128, string> Names);
}
