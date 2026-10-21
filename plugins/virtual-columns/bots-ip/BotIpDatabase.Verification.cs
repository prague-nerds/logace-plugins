using LogAce.PluginApi;
namespace LogAce.Columns.bots_ip;
using System.Net;
using System.Text.RegularExpressions;

internal sealed partial class BotIpDatabase
{
    // Keep crawler identities separate even when ranges overlap. Do not use upstream
    // "match" blindly: it includes robots.txt-only tokens and misattributed identities.
    // Operator references and the deliberately limited supported set are in docs/virtual-columns.md.
    private static readonly IReadOnlyDictionary<string, Regex> VerificationTokens =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["GoogleBot Common Crawlers"] = ["Googlebot", "Googlebot-Image", "Googlebot-Video", "Storebot-Google",
                "Google-InspectionTool", "GoogleOther", "GoogleOther-Image", "GoogleOther-Video", "Google-CloudVertexBot"],
            ["BingBot"] = ["bingbot", "AdIdxBot", "MicrosoftPreview"],
            ["AhrefsBot"] = ["AhrefsBot"],
            ["AppleBot"] = ["Applebot"],
            ["OAI-SearchBot"] = ["OAI-SearchBot"],
            ["ChatGPT-User"] = ["ChatGPT-User"],
            ["GPTBot"] = ["GPTBot"],
            ["DuckDuck Bot"] = ["DuckDuckBot"],
            ["Perplexity AI"] = ["PerplexityBot"]
        }.ToDictionary(pair => pair.Key, pair => new Regex(
            @"(?:^|[\s;(])(?:" + string.Join('|', pair.Value.Select(Regex.Escape)) + @")(?=[/\s;)]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)), StringComparer.OrdinalIgnoreCase);

    public string Verify(string userAgent, IPAddress address)
    {
        VerificationRule? claimed = null;
        try
        {
            foreach (VerificationRule rule in _verificationRules)
            {
                if (!rule.UserAgent.IsMatch(userAgent)) continue;
                // Multiple independent identities in one UA are ambiguous.
                if (claimed is not null) return "unknown";
                claimed = rule;
            }
        }
        catch (RegexMatchTimeoutException) { return "unknown"; }

        return claimed is null ? "unknown" : claimed.Ranges.Match(address) is not null ? "verified" : "unverified";
    }

    private sealed record VerificationRule(Regex UserAgent, BotIpDatabase Ranges);

    private sealed class VerificationRangeBuilder
    {
        public Dictionary<int, Dictionary<UInt128, SortedSet<string>>> Ipv4 { get; } = new();
        public Dictionary<int, Dictionary<UInt128, SortedSet<string>>> Ipv6 { get; } = new();
    }
}
