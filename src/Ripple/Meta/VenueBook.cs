using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ripple.Meta;

/// <summary>
/// data/venues.json, loaded. §9.2 is explicit that this table stays in DATA rather than code:
/// the release series and the shows worth naming are exactly the kind of list that grows, and
/// a user extending it should not need a rebuild.
///
/// Three lookups, each independently allowed to miss — the caption degrades to date-only and
/// then to nothing, and none of that is an error.
/// </summary>
public sealed class VenueBook
{
    private readonly Dictionary<string, string> _byDate = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _bySeries = new(StringComparer.Ordinal);
    private readonly List<string> _seriesPrefixes = [];

    /// <summary>An empty book. Every lookup misses, which is a supported state.</summary>
    public static VenueBook Empty { get; } = new();

    public int DateCount => _byDate.Count;
    public int SeriesCount => _bySeries.Count + _seriesPrefixes.Count;

    /// <summary>
    /// Load, or return an empty book. Never throws: a malformed venues.json must cost the
    /// caption its venue text, not take the app down.
    /// </summary>
    public static VenueBook Load(string dataRoot)
    {
        var book = new VenueBook();
        string path = Path.Combine(dataRoot, "venues.json");
        try
        {
            if (!File.Exists(path)) return book;
            var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject;
            if (root is null) return book;

            if (root["byDate"] is JsonObject bd)
                foreach (var kv in bd)
                    if ((string?)kv.Value is { } s) book._byDate[kv.Key] = s;

            if (root["bySeries"] is JsonObject bs)
                foreach (var kv in bs)
                    if ((string?)kv.Value is { } s)
                        book._bySeries[TrackParser.Normalize(kv.Key)] = s;

            if (root["seriesPrefixes"] is JsonArray sp)
                foreach (var n in sp)
                    if ((string?)n is { } s)
                        book._seriesPrefixes.Add(TrackParser.Normalize(s));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[meta] venues.json: {ex.Message} — captions will be "
                + "date-only");
        }
        return book;
    }

    /// <summary>Caption text for an exact show date, or null.</summary>
    public string? VenueForDate(DateOnly d)
        => _byDate.TryGetValue(d.ToString("yyyy-MM-dd"), out var v) ? v : null;

    /// <summary>
    /// The release series a string belongs to, normalized, or null.
    ///
    /// `bySeries` keys are checked before `seriesPrefixes`, because the former carry a venue
    /// and the latter only mark the series — "Cornell" should win over "Dave's Picks" on a
    /// string containing both, since it is the one that knows where the show was.
    /// </summary>
    public string? MatchSeries(string? text)
    {
        string t = TrackParser.Normalize(text);
        if (t.Length == 0) return null;

        foreach (var key in _bySeries.Keys)
            if (t.Contains(key, StringComparison.Ordinal)) return key;

        foreach (var prefix in _seriesPrefixes)
            if (t.Contains(prefix, StringComparison.Ordinal)) return prefix;

        return null;
    }

    /// <summary>Caption text for a matched series, or null.</summary>
    public string? SeriesCaption(string series)
        => _bySeries.TryGetValue(series, out var v) ? v : null;

    /// <summary>
    /// A date implied by a series entry, when its caption carries one — the vault releases
    /// name a specific show. Parsed out of the caption rather than duplicated as a field, so
    /// the data file stays the shape it already is and a user adding a row gets the date for
    /// free by writing it the way the existing rows do.
    /// </summary>
    public DateOnly? SeriesDate(string series)
    {
        if (!_bySeries.TryGetValue(series, out var caption)) return null;
        var (d, from) = TrackParser.ExtractDate(caption);
        // Only a real day counts. "Europe 1972" gives a bare year, which is not a show date
        // and must not become one.
        return from is DateSource.Iso or DateSource.Numeric ? d : null;
    }
}
