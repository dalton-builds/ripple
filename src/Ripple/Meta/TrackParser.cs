using System.Text;
using System.Text.RegularExpressions;

namespace Ripple.Meta;

/// <summary>
/// §9.2, and deliberately a PURE function of three strings plus the venue book.
///
/// No SMTC types, no GL, no clock. Spotify's Dead catalog is uneven and the interesting cases
/// are all string-shaped — "Dark Star - Live at Barton Hall, Cornell University, Ithaca, NY,
/// 5/8/77", "Dick's Picks Vol. 8", a studio album with nothing at all — so the parser has to
/// be testable against a fixture list of real strings rather than reachable only by playing
/// music. `TrackParserTests` is that list.
///
/// THE GOVERNING RULE IS THAT A FAILURE DEGRADES, NEVER GUESSES. Every extraction is allowed
/// to return nothing, and returning nothing puts the track in era `unknown` with no song mode,
/// which is a state the renderer handles fully. A wrong date is worse than no date: it picks
/// the wrong era, and someone who knows the music sees a 1972 palette on an 1989 show.
/// </summary>
public sealed partial class TrackParser(VenueBook venues)
{
    private readonly VenueBook _venues = venues;

    // ------------------------------------------------------------ artist gate

    /// <summary>
    /// §9.2's extended family. Matched as a normalized SUBSTRING, because Spotify credits
    /// vary — "Grateful Dead", "The Grateful Dead", "Jerry Garcia Band", "Garcia, Jerry".
    ///
    /// Ordered longest-first so "jerry garcia band" is tested before "jerry garcia": both
    /// match, but the specific one is the honest answer for the diagnostics.
    /// </summary>
    private static readonly string[] Family =
    [
        "joe russos almost dead", "dark star orchestra", "new riders of the purple sage",
        "jerry garcia acoustic band", "legion of mary", "reconstruction",
        "billy and the kids", "billy the kid", "dead and company", "phil lesh and friends",
        "jerry garcia band", "grateful dead", "jerry garcia", "bob weir", "ratdog",
        "phil lesh", "furthur", "the dead", "jrad", "jgb", "bobby weir",
        "mickey hart", "bill kreutzmann", "donna jean", "brent mydland",
    ];

    /// <summary>
    /// Bands that contain a family name as a substring but are NOT the family. Checked first.
    /// Without this, "Dead Kennedys" and "Deadmau5" both pass a naive "dead" test — and the
    /// gate exists precisely to keep a non-Dead record out of an era treatment.
    /// </summary>
    private static readonly string[] NotFamily =
    [
        "dead kennedys", "deadmau5", "dead weather", "the walking dead", "grateful dead tribute",
        "dead can dance", "deadmaus", "dead prez", "dead cross",
    ];

    public static bool IsFamilyArtist(string? artist)
    {
        string a = Normalize(artist);
        if (a.Length == 0) return false;
        foreach (var no in NotFamily) if (a.Contains(no, StringComparison.Ordinal)) return false;
        foreach (var yes in Family) if (a.Contains(yes, StringComparison.Ordinal)) return true;
        return false;
    }

    // ------------------------------------------------------------ date regexes

    // Order matters: the most specific pattern wins, and each is anchored enough not to
    // match a track number or a bitrate.
    [GeneratedRegex(@"\b(19[6-9]\d)[-/.](\d{1,2})[-/.](\d{1,2})\b")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"\b(\d{1,2})[-/.](\d{1,2})[-/.]((?:19)?\d{2})\b")]
    private static partial Regex NumericDate();

    [GeneratedRegex(@"['’](\d{2})\b")]
    private static partial Regex ApostropheYear();

    [GeneratedRegex(@"\b(19[6-9]\d)\b")]
    private static partial Regex BareYear();

    // ------------------------------------------------ title normalization

    [GeneratedRegex(@"\s*[-–—]\s*live\s+(at|from|in)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex LiveAtSuffix();

    [GeneratedRegex(@"\s*[\(\[](live|remaster(ed)?|mono|stereo|single version|album version|" +
                    @"\d{4}\s+remaster(ed)?|remastered\s+\d{4}|bonus track|edit|reprise)[^\)\]]*[\)\]]",
                    RegexOptions.IgnoreCase)]
    private static partial Regex ParenNoise();

    [GeneratedRegex(@"\s*[-–—]\s*(remaster(ed)?|\d{4}\s+remaster(ed)?|live|single version)\b.*$",
                    RegexOptions.IgnoreCase)]
    private static partial Regex DashNoise();

    [GeneratedRegex(@"\s*[,\-–—]?\s*\b(pt\.?|part)\s*\.?\s*([0-9]+|[ivx]+)\b\s*$",
                    RegexOptions.IgnoreCase)]
    private static partial Regex PartSuffix();

    [GeneratedRegex(@"\s*[,\-–—]?\s*\b\d{1,2}[-/.]\d{1,2}[-/.](?:19)?\d{2}\b\s*$")]
    private static partial Regex TrailingNumericDate();

    [GeneratedRegex(@"\s*[,\-–—]?\s*\b19[6-9]\d[-/.]\d{1,2}[-/.]\d{1,2}\b\s*$")]
    private static partial Regex TrailingIsoDate();

    [GeneratedRegex(@"[^a-z0-9 ]")]
    private static partial Regex NonAlnum();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // ------------------------------------------------------------------ parse

    /// <summary>
    /// The whole of §9.2 in one call. Never throws and never returns null: a track it cannot
    /// make sense of comes back as a valid record in era <see cref="EraMap.Unknown"/>.
    /// </summary>
    public TrackInfo Parse(string? title, string? artist, string? album)
    {
        title ??= ""; artist ??= ""; album ??= "";

        bool family = IsFamilyArtist(artist);

        // Segues split BEFORE normalization, because the separator is punctuation that
        // normalization would eat.
        var segue = SplitSegue(title);
        string primary = segue.Length > 0 ? segue[0] : title;
        string songKey = NormalizeTitle(primary);

        // Date and series only mean anything inside the family. A 1972 jazz record is not an
        // Europe '72 show, and the gate is the only thing that knows the difference.
        DateOnly? date = null;
        var from = DateSource.None;
        string? series = null;
        string? venue = null;

        if (family)
        {
            // Album first, then title: the release carries the show, and a track title that
            // happens to contain a year is weaker evidence than the album it sits on.
            (date, from) = ExtractDate(album);
            if (from == DateSource.None) (date, from) = ExtractDate(title);

            series = _venues.MatchSeries(album) ?? _venues.MatchSeries(title);

            // A series with a known date fills in where no date parsed. Never overrides a
            // parsed one: "Dick's Picks Vol. 8 (1970-05-02)" should use the real date.
            if (series is not null)
            {
                venue = _venues.SeriesCaption(series);
                if (from == DateSource.None)
                {
                    var sd = _venues.SeriesDate(series);
                    if (sd is not null) { date = sd; from = DateSource.Series; }
                }
            }

            venue ??= date is { } d ? _venues.VenueForDate(d) : null;
        }

        int era = family ? EraMap.FromDate(date) : EraMap.Unknown;

        return new TrackInfo
        {
            Title = title,
            Artist = artist,
            Album = album,
            IsFamily = family,
            ShowDate = date,
            DateFrom = from,
            Venue = venue,
            Series = series,
            SongKey = family ? songKey : "",
            SegueSongs = segue.Select(NormalizeTitle).Where(s => s.Length > 0).ToArray(),
            EraId = era,
            Caption = BuildCaption(primary, artist, date, from, venue),
            Slug = Slugify(artist, title, album),
        };
    }

    // ------------------------------------------------------------------ pieces

    /// <summary>
    /// Split a segue title. `>` and `->` are the Dead's own notation and Spotify preserves
    /// them. Returns a single element for an ordinary title.
    /// </summary>
    public static string[] SplitSegue(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return [];
        var parts = title.Split(["->", "→", ">"], StringSplitOptions.None)
                         .Select(p => p.Trim())
                         .Where(p => p.Length > 0)
                         .ToArray();
        return parts.Length == 0 ? [title.Trim()] : parts;
    }

    /// <summary>
    /// §9.2's title normalization, then the song-mode key. Applied in a fixed order because
    /// the patterns overlap: "- Live at X (Remastered)" needs the parenthetical gone before
    /// the dash suffix is anchored at end-of-string.
    /// </summary>
    public static string NormalizeTitle(string? title)
    {
        string s = title ?? "";
        s = ParenNoise().Replace(s, "");
        s = LiveAtSuffix().Replace(s, "");
        s = DashNoise().Replace(s, "");
        s = TrailingIsoDate().Replace(s, "");
        s = TrailingNumericDate().Replace(s, "");
        s = PartSuffix().Replace(s, "");
        return Normalize(s);
    }

    /// <summary>
    /// The title as a HUMAN should read it: the same noise stripped as
    /// <see cref="NormalizeTitle"/> removes, but with capitalization and punctuation intact.
    ///
    /// The caption needs this and the song-mode key must not have it. Built from the raw title
    /// the caption came out as "Dark Star - Live at Old Renaissance Faire Grounds, Veneta, OR,
    /// 8/27/72 · Grateful Dead · Old Renaissance Faire Grounds, Veneta OR · 8/27/72" — the
    /// venue and the date twice each, and far too long to read from three feet, which is the
    /// one thing §8.5 asks of it.
    /// </summary>
    public static string CleanDisplayTitle(string? title)
    {
        string s = title ?? "";
        s = ParenNoise().Replace(s, "");
        s = LiveAtSuffix().Replace(s, "");
        s = DashNoise().Replace(s, "");
        s = TrailingIsoDate().Replace(s, "");
        s = TrailingNumericDate().Replace(s, "");
        s = PartSuffix().Replace(s, "");
        return Whitespace().Replace(s, " ").Trim().Trim(',', '-', '–', '—').Trim();
    }

    /// <summary>Lowercase, strip punctuation and diacritics, collapse whitespace.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        // Decompose so accented characters lose their marks rather than their letters.
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);

        string t = sb.ToString().ToLowerInvariant();

        // Apostrophes are DELETED, not turned into spaces. They sit inside words — "Franklin's
        // Tower", "Truckin'", "Joe Russo's Almost Dead", "Dick's Picks" — and replacing them
        // with a separator gives "franklin s tower", which matches neither data/songs.json's
        // keys nor the artist gate. Every flavour of apostrophe Spotify serves, including the
        // typographic one.
        t = t.Replace("'", "").Replace("’", "").Replace("ʼ", "").Replace("`", "");

        // Ampersands are a word, and the difference matters: "Dead & Company" has to reach
        // "dead and company" to match the family list, not "dead company".
        t = t.Replace("&", " and ");

        t = NonAlnum().Replace(t, " ");
        return Whitespace().Replace(t, " ").Trim();
    }

    /// <summary>
    /// §9.2's four date patterns, most specific first. Returns <see cref="DateSource.None"/>
    /// rather than guessing whenever the match is not a plausible show date.
    /// </summary>
    public static (DateOnly? Date, DateSource From) ExtractDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, DateSource.None);

        if (IsoDate().Match(text) is { Success: true } iso)
        {
            var d = MakeDate(int.Parse(iso.Groups[1].Value),
                             int.Parse(iso.Groups[2].Value),
                             int.Parse(iso.Groups[3].Value));
            if (d is not null) return (d, DateSource.Iso);
        }

        if (NumericDate().Match(text) is { Success: true } num)
        {
            int a = int.Parse(num.Groups[1].Value);
            int b = int.Parse(num.Groups[2].Value);
            int y = ExpandYear(int.Parse(num.Groups[3].Value));
            // US order, which is what the Dead community writes and what Spotify carries.
            var d = MakeDate(y, a, b);
            if (d is not null) return (d, DateSource.Numeric);
        }

        if (ApostropheYear().Match(text) is { Success: true } ap)
        {
            int y = ExpandYear(int.Parse(ap.Groups[1].Value));
            if (y is >= 1960 and <= 1999) return (new DateOnly(y, 1, 1), DateSource.YearOnly);
        }

        if (BareYear().Match(text) is { Success: true } bare)
        {
            int y = int.Parse(bare.Groups[1].Value);
            return (new DateOnly(y, 1, 1), DateSource.BareYear);
        }

        return (null, DateSource.None);
    }

    /// <summary>
    /// Two-digit years are 19xx here, always. The band's active range is 1965-1995 and there
    /// is no 20xx reading of "'72" that means anything in this catalogue.
    /// </summary>
    private static int ExpandYear(int y) => y >= 100 ? y : 1900 + y;

    /// <summary>
    /// Build a date, or nothing. Rejects anything outside the plausible range or not a real
    /// calendar date — a malformed match must produce no date rather than a wrong one.
    /// </summary>
    private static DateOnly? MakeDate(int year, int month, int day)
    {
        if (year is < 1960 or > 1999) return null;
        if (month is < 1 or > 12 || day is < 1 or > 31) return null;
        try { return new DateOnly(year, month, day); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>
    /// §8.5's caption: "Dark Star · Veneta, OR · 8/27/72". The date is only shown when it is
    /// an actual DAY — a bare year parsed off a studio album is not a show date and printing
    /// "· 1970" beside a song title implies a precision that is not there.
    /// </summary>
    private static string BuildCaption(string title, string artist, DateOnly? date,
                                       DateSource from, string? venue)
    {
        string t = CleanDisplayTitle(title);
        if (t.Length == 0) return "";

        var parts = new List<string> { t };
        if (!string.IsNullOrWhiteSpace(artist)) parts.Add(artist.Trim());

        // The venue is trimmed to its place — "Old Renaissance Faire Grounds, Veneta OR"
        // becomes "Veneta, OR". §8.5's example is "Dark Star · Veneta, OR · 8/27/72": the hall
        // name is what makes the line too long to read, and the town is what a listener
        // recognises.
        if (venue is not null) parts.Add(ShortVenue(venue));

        if (date is { } d && from is DateSource.Iso or DateSource.Numeric or DateSource.Series)
            parts.Add($"{d.Month}/{d.Day}/{d.Year % 100:00}");

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The last comma-separated element of a venue string, which is where the town and state
    /// live in every row of data/venues.json — "Barton Hall, Cornell University, Ithaca NY"
    /// gives "Ithaca, NY". Falls back to the whole string when there is no comma.
    ///
    /// A trailing two-letter state gets its comma back, because "Ithaca NY" reads as a typo
    /// and "Ithaca, NY" reads as a place.
    /// </summary>
    public static string ShortVenue(string venue)
    {
        // Vault entries carry their own date after an em dash; the caption adds one already.
        string v = venue.Split('—')[0].Trim().TrimEnd(',').Trim();
        var parts = v.Split(',', StringSplitOptions.RemoveEmptyEntries
                               | StringSplitOptions.TrimEntries);
        string last = parts.Length > 0 ? parts[^1] : v;

        int sp = last.LastIndexOf(' ');
        if (sp > 0 && last.Length - sp - 1 == 2 && last[(sp + 1)..].All(char.IsUpper))
            return $"{last[..sp]}, {last[(sp + 1)..]}";
        return last;
    }

    /// <summary>
    /// A stable filesystem-safe identity, for auto-capture dedupe and filenames. Includes the
    /// album because the same song across two releases is different material and worth
    /// capturing twice.
    /// </summary>
    public static string Slugify(string artist, string title, string album)
    {
        string s = Normalize($"{artist} {title} {album}");
        s = s.Replace(' ', '-');
        return s.Length <= 96 ? s : s[..96].TrimEnd('-');
    }

    /// <summary>
    /// Resolve the song-mode key against the loaded map, honouring segues.
    ///
    /// Takes a predicate rather than the map, so this file stays free of Ripple.Presets types
    /// and remains testable with nothing but a set of strings — the parser is the one part of
    /// the metadata layer that can be tested without playing music, and that is worth keeping.
    ///
    /// Segue order is song order: "China Cat Sunflower > I Know You Rider" tries china cat
    /// first, and falls through to the rider if only the second has a mode.
    /// </summary>
    public static string? MatchSongMode(TrackInfo t, Func<string, bool> hasMode)
    {
        if (!t.IsFamily) return null;
        var candidates = t.SegueSongs.Length > 0 ? t.SegueSongs : [t.SongKey];
        foreach (var candidate in candidates)
            if (candidate.Length > 0 && hasMode(candidate)) return candidate;
        return null;
    }
}
