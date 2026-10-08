using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkerControl.Service.Traffic;

/// <summary>
/// One request, as the reverse proxy wrote it down.
/// </summary>
internal sealed record Hit(DateTimeOffset At, string Ip, string Host, string Method, string Path, int Status, long Bytes, double Seconds, string Upstream, string Referer = "");

/// <summary>
/// What a request is counted as: nothing that identifies one particular request is left.
/// </summary>
internal readonly record struct RouteKey(string Host, string Method, string Route, string Kind, string App, string Upstream);

internal static partial class TrafficLog
{
    /// <summary>
    /// Reads one line of the log. Null for what is not a whole line of the expected format.
    ///
    /// The format is the one of the documentation: an object with <c>t</c> (time), <c>ip</c>,
    /// <c>h</c> (host), <c>m</c> (method), <c>u</c> (address asked for), <c>s</c> (status),
    /// <c>b</c> (bytes), <c>rt</c> (seconds) and <c>ua</c> (who answered behind the proxy).
    /// </summary>
    public static Hit? Parse(ReadOnlySpan<byte> line)
    {
        try
        {
            var reader = new Utf8JsonReader(line);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            string Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            double Number(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

            if (!DateTimeOffset.TryParse(Text("t"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                return null;
            var address = Text("u");
            if (address.Length == 0)
                return null;

            // After a retry the proxy lists everybody it tried; the last one is who answered.
            var upstream = Text("ua");
            var comma = upstream.LastIndexOf(',');
            if (comma >= 0)
                upstream = upstream[(comma + 1)..].Trim();

            // The same instance, whichever way the proxy reached this machine.
            if (upstream.StartsWith("[::1]:", StringComparison.Ordinal))
                upstream = "127.0.0.1:" + upstream[6..];
            else if (upstream.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase))
                upstream = "127.0.0.1:" + upstream[10..];

            var query = address.IndexOf('?');
            return new Hit(at, Text("ip"), Text("h").ToLowerInvariant(), Text("m").ToUpperInvariant(), query >= 0 ? address[..query] : address,
                (int)Number("s"), (long)Number("b"), Number("rt"), upstream == "-" ? "" : upstream, Text("ref"));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static readonly HashSet<string> StaticExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "js", "mjs", "css", "map", "html", "htm", "json", "txt", "xml", "webmanifest",
        "png", "jpg", "jpeg", "gif", "svg", "ico", "webp", "avif", "bmp",
        "woff", "woff2", "ttf", "eot", "otf", "mp3", "mp4", "webm", "wav", "pdf", "apk", "zip"
    };

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex Digits();

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex Guid();

    [GeneratedRegex(@"^[0-9a-fA-F]{16,}$")]
    private static partial Regex Hex();

    /// <summary>
    /// A path without what makes it one of a kind: numbers, identifiers and anything long
    /// or encoded become <c>{id}</c>, so that every call to the same endpoint counts as one.
    /// A file is counted by its folder and kind, not by its name.
    /// </summary>
    public static (string Route, string Kind, string App) Normalize(string path, IReadOnlyList<string> groupBy, IReadOnlyList<string[]> templates)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 12)
            segments = segments[..12];

        var app = segments.Length == 0
            ? "(raiz)"
            : segments.Length > 1 && groupBy.Contains(segments[0], StringComparer.OrdinalIgnoreCase)
                ? segments[0].ToLowerInvariant() + "/" + Clean(segments[1]).ToLowerInvariant()
                : Clean(segments[0]).ToLowerInvariant();

        // A file: what matters is where it is and what it is.
        if (segments.Length > 0)
        {
            var name = segments[^1];
            var dot = name.LastIndexOf('.');
            if (dot > 0 && dot < name.Length - 1 && StaticExtensions.Contains(name[(dot + 1)..]))
            {
                var folder = string.Join('/', segments[..^1].Select(Clean));
                // A file at the root belongs to the application around everything else.
                return ((folder.Length > 0 ? "/" + folder : "") + "/*." + name[(dot + 1)..].ToLowerInvariant(), "static", segments.Length == 1 ? "(raiz)" : app);
            }
        }

        foreach (var template in templates)
        {
            if (Matches(template, segments))
                return ("/" + string.Join('/', template), "api", app);
        }

        // A file asked of an application by its name (a download): the name is one of a kind.
        var route = segments.Select(Clean).ToArray();
        if (route.Length > 1 && route[^1] != "{id}" && FileName().IsMatch(route[^1]))
            route[^1] = "{arquivo}";
        return ("/" + string.Join('/', route), "api", app);
    }

    [GeneratedRegex(@"\.[A-Za-z0-9]{1,5}$")]
    private static partial Regex FileName();

    /// <summary>
    /// The screen a request came from, out of the address the browser says it was on: the
    /// site and the path, with what identifies one particular visit taken out like in a
    /// route. Null when the browser said nothing, or said something that is not a page.
    /// </summary>
    public static (string Host, string Page)? Page(string referer)
    {
        if (referer.Length == 0 || !Uri.TryCreate(referer, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
            return null;
        var segments = address.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 8)
            segments = segments[..8];
        return (address.Host.ToLowerInvariant(), "/" + string.Join('/', segments.Select(Clean)));
    }

    /// <summary>
    /// Routes written by hand, split once: <c>/api/orders/{code}</c> is three segments, the
    /// last of which takes anything.
    /// </summary>
    public static List<string[]> Templates(IEnumerable<string> routes) =>
        [.. routes.Select(route => route.Split('/', StringSplitOptions.RemoveEmptyEntries)).Where(segments => segments.Length > 0)];

    private static bool Matches(string[] template, string[] segments)
    {
        if (template.Length != segments.Length)
            return false;
        for (var i = 0; i < template.Length; i++)
        {
            var wild = template[i].Length > 2 && template[i][0] == '{' && template[i][^1] == '}';
            if (!wild && !string.Equals(template[i], segments[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static string Clean(string segment)
    {
        if (Digits().IsMatch(segment) || Guid().IsMatch(segment) || Hex().IsMatch(segment))
            return "{id}";
        // Encoded text, or something too long to be the name of anything.
        if (segment.Length > 40 || segment.Contains('%'))
            return "{id}";
        // Mostly digits with something else: a code, a date, a document number.
        var digits = segment.Count(char.IsDigit);
        if (digits >= 4 && digits * 2 >= segment.Length)
            return "{id}";
        return segment;
    }
}
