using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LogAce.PluginApi;
namespace LogAce.Columns.http;
internal static class VirtualColumnTransforms
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    public static Func<LogField[], LogField> Create(ColumnKind kind, IReadOnlyDictionary<string, string> parameters, string[] sources)
    {
        return kind switch
        {
            ColumnKind.UrlRoute => Route(parameters),
            ColumnKind.ResourceType => ResourceType(parameters),
            ColumnKind.TrafficSource => TrafficSource(parameters),
            _ => throw new InvalidOperationException("Unsupported virtual transform.")};
    }

    private static Func<LogField[], LogField> Route(IReadOnlyDictionary<string, string> parameters)
    {
        Regex? pattern = null;
        string? replacement = null;
        if (parameters.ContainsKey("pattern") != parameters.ContainsKey("replacement"))
            throw new InvalidOperationException("url-route requires both pattern and replacement when using a custom rule.");
        if (parameters.TryGetValue("pattern", out string? rawPattern))
        {
            if (string.IsNullOrWhiteSpace(rawPattern))
                throw new InvalidOperationException("url-route pattern cannot be empty.");
            pattern = new Regex(rawPattern, RegexOptions.CultureInvariant, RegexTimeout);
            replacement = parameters["replacement"];
        }

        return fields =>
        {
            string path = UrlPath(fields[0].RawText);
            if (path.Length == 0)
                return new LogField(string.Empty);
            if (pattern is not null)
            {
                try
                {
                    return new LogField(pattern.Replace(path, replacement!));
                }
                catch (RegexMatchTimeoutException)
                {
                    return new LogField("#ERROR: route regex timed out");
                }
            }

            string[] parts = path.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = Uri.UnescapeDataString(parts[i]);
                if (part.Length > 0 && part.All(char.IsAsciiDigit))
                    parts[i] = "{id}";
                else if (Guid.TryParseExact(part, "D", out _) || Guid.TryParseExact(part, "N", out _))
                    parts[i] = "{uuid}";
                else if (part.Length >= 16 && part.All(char.IsAsciiHexDigit))
                    parts[i] = "{hash}";
            }

            return new LogField(string.Join('/', parts));
        };
    }

    private static Func<LogField[], LogField> ResourceType(IReadOnlyDictionary<string, string> parameters)
    {
        string[] prefixes = Split(parameters.GetValueOrDefault("api-prefixes", "/api/,/graphql"));
        if (prefixes.Any(prefix => !prefix.StartsWith('/')))
            throw new InvalidOperationException("api-prefixes must contain URL paths beginning with '/'.");
        return fields =>
        {
            string path = UrlPath(fields[0].RawText);
            string mime = fields.Length > 1 ? fields[1].RawText.Split(';')[0].Trim().ToLowerInvariant() : string.Empty;
            if (prefixes.Any(prefix => path.Equals(prefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)))
                return new LogField("api");
            if (mime.StartsWith("image/"))
                return new LogField("image");
            if (mime is "text/javascript" or "application/javascript" or "application/ecmascript")
                return new LogField("script");
            if (mime == "text/css")
                return new LogField("style");
            if (mime.StartsWith("font/") || mime.Contains("font"))
                return new LogField("font");
            if (mime.StartsWith("audio/") || mime.StartsWith("video/"))
                return new LogField("media");
            if (mime is "application/json" || mime.EndsWith("+json"))
                return new LogField("api");
            if (mime is "text/html" or "application/xhtml+xml" or "application/pdf" or "text/plain")
                return new LogField("document");
            if (mime.Length > 0 && mime != "-")
                return new LogField("other");
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return new LogField(extension switch
            {
                ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".webp" or ".avif" or ".ico" => "image",
                ".js" or ".mjs" or ".cjs" => "script",
                ".css" => "style",
                ".woff" or ".woff2" or ".ttf" or ".otf" or ".eot" => "font",
                ".mp4" or ".webm" or ".mp3" or ".ogg" or ".wav" => "media",
                ".json" => "api",
                ".html" or ".htm" or ".aspx" or ".php" or ".pdf" or ".txt" => "document",
                "" when path.Length > 0 => "document",
                _ => path.Length == 0 ? string.Empty : "other"
            });
        };
    }

    private static Func<LogField[], LogField> TrafficSource(IReadOnlyDictionary<string, string> parameters)
    {
        string[] internalDomains = Domains(parameters["internal"]);
        if (internalDomains.Length == 0)
            throw new InvalidOperationException("traffic-source requires at least one internal domain.");
        string[] search = Domains(parameters.GetValueOrDefault("search", "google.com,google.cz,google.co.uk,google.de,bing.com,search.yahoo.com,duckduckgo.com,search.seznam.cz"));
        string[] social = Domains(parameters.GetValueOrDefault("social", "facebook.com,instagram.com,x.com,t.co,twitter.com,linkedin.com,reddit.com,pinterest.com,tiktok.com"));
        return fields =>
        {
            string raw = fields[0].RawText.Trim();
            if (raw.Length == 0 || raw == "-")
                return new LogField("no-referrer");
            if (raw.StartsWith('/') && !raw.StartsWith("//"))
                return new LogField("internal");
            if (!TryWebUri(raw, out Uri? uri))
                return new LogField("unknown");
            string host = uri!.IdnHost.TrimEnd('.');
            return new LogField(internalDomains.Any(domain => DomainMatches(host, domain)) ? "internal" : search.Any(domain => DomainMatches(host, domain)) ? "search" : social.Any(domain => DomainMatches(host, domain)) ? "social" : "external");
        };
    }

    private static string[] Domains(string raw) => Split(raw).Select(value =>
    {
        if (!TryWebUri(value.Contains("://", StringComparison.Ordinal) ? value : "https://" + value, out Uri? uri) || uri!.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
            throw new InvalidOperationException($"Invalid domain '{value}'. Supply host names, separated by commas.");
        return uri.IdnHost.TrimEnd('.');
    }).ToArray();
    private static bool DomainMatches(string host, string domain) => host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    internal static string UrlPath(string raw)
    {
        string text = raw.Trim();
        if (text.Length == 0 || text == "-")
            return string.Empty;
        // IIS/Apache profiles may expose either the target or a complete request line.
        int space = text.IndexOf(' ');
        if (space > 0 && text.Contains(" HTTP/", StringComparison.OrdinalIgnoreCase))
        {
            int end = text.LastIndexOf(" HTTP/", StringComparison.OrdinalIgnoreCase);
            if (end <= space + 1)
                return string.Empty;
            text = text[(space + 1)..end];
        }

        if (TryWebUri(text, out Uri? uri))
            return uri!.AbsolutePath;
        if (text.Contains("://", StringComparison.Ordinal))
            return string.Empty;
        return text.Split('?', '#')[0];
    }

    private static bool TryWebUri(string text, out Uri? uri) => Uri.TryCreate(text.StartsWith("//") ? "https:" + text : text, UriKind.Absolute, out uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0;
    private static string[] Split(string value) => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
