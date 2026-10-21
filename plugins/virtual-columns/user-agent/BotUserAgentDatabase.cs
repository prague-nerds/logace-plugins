using LogAce.PluginApi;
namespace LogAce.Columns.user_agent;
using System.Text.Json;
using System.Text.RegularExpressions;
using UaDetector.Abstractions.Enums;



internal sealed class BotUserAgentDatabase
{
    // UaDetector's LGPL-3.0 database already converts the Matomo expressions to .NET syntax.
    internal const string SourceUrl = "https://raw.githubusercontent.com/nandor23/UaDetector/main/src/UaDetector/Resources/bots.json";
    private const int MaximumRuleCount = 20_000;
    private const int MaximumPatternLength = 16_384;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private readonly BotRule[] _rules;

    private BotUserAgentDatabase(BotRule[] rules)
    {
        _rules = rules;
    }

    public int Count => _rules.Length;

    public static BotUserAgentDatabase Parse(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using JsonDocument document = JsonDocument.Parse(payload.TrimStart('\uFEFF'));
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() is 0 or > MaximumRuleCount)
        {
            throw new InvalidDataException("The bot User-Agent database must contain a non-empty list of rules.");
        }

        var rules = new List<BotRule>(root.GetArrayLength());
        foreach (JsonElement entry in root.EnumerateArray())
        {
            string pattern = ReadRequiredString(entry, "regex", MaximumPatternLength);
            string name = ReadRequiredString(entry, "name", 1024);
            string category = ReadCategory(entry);

            Regex regex;
            try
            {
                // Keep UaDetector's prefix boundary so a token inside a browser/product name is not a bot.
                regex = new Regex(
                    $"(?:^|[^A-Z0-9_-]|[^A-Z0-9-]_|sprd-|MZ-)(?:{pattern})",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    MatchTimeout);
                if (regex.IsMatch(string.Empty))
                {
                    throw new InvalidDataException($"The bot rule for '{name}' matches an empty User-Agent.");
                }
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException($"The bot rule for '{name}' contains an invalid regular expression.", ex);
            }
            catch (RegexMatchTimeoutException ex)
            {
                throw new InvalidDataException($"The bot rule for '{name}' exceeded the regular expression time limit.", ex);
            }

            rules.Add(new BotRule(regex, name, category));
        }

        return new BotUserAgentDatabase(rules.ToArray());
    }

    public BotUserAgentMatch? Match(string userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return null;
        }

        foreach (BotRule rule in _rules)
        {
            try
            {
                Match match = rule.Regex.Match(userAgent);
                if (match.Success)
                {
                    return new BotUserAgentMatch(match.Result(rule.Name).Trim(), rule.Category);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // An unusual log value must not stall processing or crash a background lookup.
                return null;
            }
        }

        return null;
    }

    private static string ReadRequiredString(JsonElement entry, string property, int maximumLength)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty(property, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString())
            || value.GetString()!.Length > maximumLength)
        {
            throw new InvalidDataException($"A bot User-Agent rule has a missing or invalid '{property}' value.");
        }

        return value.GetString()!;
    }

    private static string ReadCategory(JsonElement entry)
    {
        if (!entry.TryGetProperty("category", out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int category) || category <= 0)
        {
            throw new InvalidDataException("A bot User-Agent rule has an invalid category.");
        }

        // Future upstream categories can still identify the bot without inventing a category name.
        return Enum.GetName((BotCategory)category) ?? string.Empty;
    }

    private sealed record BotRule(Regex Regex, string Name, string Category);
}
