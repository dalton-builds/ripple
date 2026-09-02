using Ripple.Meta;
using Xunit;
using Xunit.Abstractions;

namespace Ripple.Tests.Meta;

/// <summary>
/// §9.2 against a fixture list of REAL Spotify strings.
///
/// This is the file that keeps the metadata layer honest, because the parser is the only part
/// of Phase 3 that can be tested without playing music, and it is where a wrong answer is most
/// expensive: a bad date picks a wrong era, and someone who knows the catalogue sees a 1972
/// palette on a 1989 show. The governing assertion throughout is that **failure degrades to
/// era `unknown` rather than producing a wrong date.**
///
/// The strings below are the shapes Spotify actually serves — including the messy ones, which
/// are the point. A parser tested only on "Dark Star - Live at Barton Hall ... 5/8/77" passes
/// and then meets a studio album.
/// </summary>
public sealed class TrackParserTests(ITestOutputHelper log)
{
    private static TrackParser Make()
        => new(VenueBook.Load(FindData()));

    private static string FindData()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int up = 0; up < 8 && dir is not null; up++, dir = dir.Parent)
        {
            string cand = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(cand, "venues.json"))) return cand;
        }
        throw new DirectoryNotFoundException("data/venues.json not found from "
            + AppContext.BaseDirectory);
    }

    // ------------------------------------------------------------ artist gate

    [Theory]
    [InlineData("Grateful Dead", true)]
    [InlineData("The Grateful Dead", true)]
    [InlineData("Jerry Garcia Band", true)]
    [InlineData("Jerry Garcia", true)]
    [InlineData("JGB", true)]
    [InlineData("Bob Weir & RatDog", true)]
    [InlineData("Phil Lesh & Friends", true)]
    [InlineData("Furthur", true)]
    [InlineData("Dead & Company", true)]
    [InlineData("Joe Russo's Almost Dead", true)]
    [InlineData("Dark Star Orchestra", true)]
    [InlineData("New Riders of the Purple Sage", true)]
    [InlineData("Legion of Mary", true)]
    // Not the family, and two of these contain a family name as a substring.
    [InlineData("Dead Kennedys", false)]
    [InlineData("deadmau5", false)]
    [InlineData("Dead Can Dance", false)]
    [InlineData("Miles Davis", false)]
    [InlineData("Phish", false)]
    [InlineData("", false)]
    public void Artist_gate_covers_the_family_and_excludes_lookalikes(string artist, bool want)
    {
        bool got = TrackParser.IsFamilyArtist(artist);
        log.WriteLine($"{artist,-32} -> {got}");
        Assert.Equal(want, got);
    }

    // ---------------------------------------------------------------- dates

    [Theory]
    [InlineData("Cornell 5/8/77", 1977, 5, 8, DateSource.Numeric)]
    [InlineData("Barton Hall, Cornell University, Ithaca, NY, 5/8/77", 1977, 5, 8, DateSource.Numeric)]
    [InlineData("1977-05-08", 1977, 5, 8, DateSource.Iso)]
    [InlineData("Dick's Picks Vol. 8: 1970-05-02", 1970, 5, 2, DateSource.Iso)]
    [InlineData("Veneta, OR 8/27/72", 1972, 8, 27, DateSource.Numeric)]
    [InlineData("Live in Europe '72", 1972, 1, 1, DateSource.YearOnly)]
    [InlineData("Workingman's Dead (1970)", 1970, 1, 1, DateSource.BareYear)]
    [InlineData("11.11.73", 1973, 11, 11, DateSource.Numeric)]
    public void Dates_parse_in_the_documented_order(string text, int y, int m, int d,
                                                    DateSource from)
    {
        var (date, source) = TrackParser.ExtractDate(text);
        log.WriteLine($"{text,-52} -> {date} via {source}");
        Assert.Equal(from, source);
        Assert.Equal(new DateOnly(y, m, d), date);
    }

    /// <summary>
    /// The important half. Each of these has something date-shaped in it and must produce NO
    /// date, because a wrong date is worse than none.
    /// </summary>
    [Theory]
    [InlineData("American Beauty")]                       // nothing at all
    [InlineData("Track 13")]                              // a number, not a year
    [InlineData("Sugar Magnolia")]
    [InlineData("2/30/77")]                               // not a real calendar date
    [InlineData("13/45/77")]                              // month and day both impossible
    [InlineData("Live at the Fillmore East, 2/11/2069")]  // outside the plausible range
    [InlineData("Remastered 2013")]                        // a remaster year is not a show date
    public void Implausible_dates_produce_nothing(string text)
    {
        var (date, source) = TrackParser.ExtractDate(text);
        log.WriteLine($"{text,-46} -> {(date?.ToString() ?? "none")} via {source}");
        // "Remastered 2013" is out of the 196x-199x window, so BareYear rejects it too.
        Assert.True(date is null, $"invented a date for '{text}': {date}");
        Assert.Equal(DateSource.None, source);
    }

    // ------------------------------------------------------- normalization

    [Theory]
    [InlineData("Dark Star - Live at Barton Hall, Cornell University, Ithaca, NY, 5/8/77", "dark star")]
    [InlineData("Truckin' [Live]", "truckin")]
    [InlineData("Ripple (Remastered)", "ripple")]
    [InlineData("Sugar Magnolia (2013 Remaster)", "sugar magnolia")]
    [InlineData("Terrapin Station - Remastered", "terrapin station")]
    [InlineData("Playing in the Band, Pt. 1", "playing in the band")]
    [InlineData("Drums Pt. 2", "drums")]
    [InlineData("Eyes of the World - 11/11/73", "eyes of the world")]
    [InlineData("Scarlet Begonias 1977-05-08", "scarlet begonias")]
    [InlineData("St. Stephen", "st stephen")]
    [InlineData("Franklin's Tower", "franklins tower")]
    public void Titles_normalize_to_the_song_mode_key(string raw, string want)
    {
        string got = TrackParser.NormalizeTitle(raw);
        log.WriteLine($"{raw,-70} -> '{got}'");
        Assert.Equal(want, got);
    }

    [Fact]
    public void Segues_split_and_match_the_first_song()
    {
        var p = Make();
        var t = p.Parse("China Cat Sunflower > I Know You Rider", "Grateful Dead",
                        "Europe '72");

        log.WriteLine($"segue={t.IsSegue} songs=[{string.Join(", ", t.SegueSongs)}] key='{t.SongKey}'");
        Assert.True(t.IsSegue);
        Assert.Equal(2, t.SegueSongs.Length);
        Assert.Equal("china cat sunflower", t.SongKey);
        Assert.Equal("i know you rider", t.SegueSongs[1]);
    }

    [Fact]
    public void A_plain_title_is_not_a_segue()
    {
        var p = Make();
        var t = p.Parse("Dark Star", "Grateful Dead", "Live/Dead");
        Assert.False(t.IsSegue);
        Assert.Equal("dark star", t.SongKey);
    }

    // -------------------------------------------------- the degradation gate

    /// <summary>
    /// §9.4's three tiers, as the three cases the plan says to test on. Each must land in a
    /// DIFFERENT and intentional state — that is the whole requirement, and "all three look
    /// plausible" is not the same as "all three look the same".
    /// </summary>
    [Fact]
    public void Three_tier_degradation_lands_in_three_different_states()
    {
        var p = Make();

        // TIER 1 — dated live release: song mode + era + caption with venue.
        var live = p.Parse("Dark Star - Live at Old Renaissance Faire Grounds, Veneta, OR, 8/27/72",
                           "Grateful Dead", "Sunshine Daydream (Live at Veneta, OR 8/27/72)");
        log.WriteLine($"live    era={live.EraId} date={live.ShowDate} song='{live.SongKey}' "
            + $"venue='{live.Venue}'");
        log.WriteLine($"        caption: {live.Caption}");

        // TIER 2 — studio album: era from a bare year at best, no venue, still a song key.
        var studio = p.Parse("Ripple", "Grateful Dead", "American Beauty");
        log.WriteLine($"studio  era={studio.EraId} date={studio.ShowDate} song='{studio.SongKey}' "
            + $"venue='{studio.Venue}'");
        log.WriteLine($"        caption: {studio.Caption}");

        // TIER 3 — non-Dead: nothing at all, and the artist gate is what guarantees it.
        var other = p.Parse("Ripple", "Some Other Band", "Ripple (1972)");
        log.WriteLine($"other   era={other.EraId} date={other.ShowDate} song='{other.SongKey}' "
            + $"venue='{other.Venue}'");
        log.WriteLine($"        caption: {other.Caption}");

        // Tier 1: full treatment.
        Assert.True(live.IsFamily);
        Assert.Equal(new DateOnly(1972, 8, 27), live.ShowDate);
        Assert.Equal(2, live.EraId);                       // Europe '72 era
        Assert.Equal("dark star", live.SongKey);
        Assert.NotNull(live.Venue);
        Assert.Contains("Veneta", live.Venue);
        Assert.Contains("8/27/72", live.Caption);

        // Tier 2: era degrades, song mode survives, no venue invented.
        Assert.True(studio.IsFamily);
        Assert.Equal("ripple", studio.SongKey);
        Assert.Null(studio.Venue);
        // No day-precision date, so the caption must not print one.
        Assert.DoesNotContain("/", studio.Caption);

        // Tier 3: the gate holds even though the title and a year both look like a match.
        Assert.False(other.IsFamily);
        Assert.Equal(EraMap.Unknown, other.EraId);
        Assert.Null(other.ShowDate);
        Assert.Equal("", other.SongKey);

        // And the three are genuinely distinct, which is the actual gate.
        Assert.NotEqual(live.EraId, other.EraId);
        Assert.NotEqual(live.SongKey, other.SongKey);
    }

    /// <summary>
    /// The messy-compilation case, called out in the prompt. A series keyword with no
    /// parseable date must still reach an era via the venue book, not guess one.
    /// </summary>
    [Theory]
    [InlineData("Dick's Picks Vol. 8", "dick's picks")]
    [InlineData("Dave's Picks Volume 14", "dave's picks")]
    [InlineData("Road Trips Vol. 2 No. 3", "road trips")]
    [InlineData("One From The Vault", "one from the vault")]
    [InlineData("Cornell 5/8/77", "cornell")]
    public void Release_series_are_recognized_from_data(string album, string expectSeriesish)
    {
        var p = Make();
        var t = p.Parse("Truckin'", "Grateful Dead", album);
        log.WriteLine($"{album,-30} series='{t.Series}' date={t.ShowDate} era={t.EraId} "
            + $"venue='{t.Venue}'");
        Assert.NotNull(t.Series);
        // Normalized, so compare loosely — the point is that SOMETHING matched from data.
        Assert.Contains(TrackParser.Normalize(expectSeriesish).Split(' ')[0], t.Series);
    }

    /// <summary>
    /// A vault release names a specific show in its caption; that date should reach the era
    /// map. "One From The Vault" is 8/13/75, which is era 3 (Terrapin).
    /// </summary>
    [Fact]
    public void A_series_with_a_known_show_supplies_the_date()
    {
        var p = Make();
        var t = p.Parse("Help on the Way", "Grateful Dead", "One From The Vault");
        log.WriteLine($"date={t.ShowDate} via {t.DateFrom} era={t.EraId} venue='{t.Venue}'");
        Assert.Equal(DateSource.Series, t.DateFrom);
        Assert.Equal(new DateOnly(1975, 8, 13), t.ShowDate);
        Assert.Equal(3, t.EraId);
    }

    /// <summary>A parsed date must win over the series' default.</summary>
    [Fact]
    public void A_parsed_date_beats_the_series_date()
    {
        var p = Make();
        var t = p.Parse("Dark Star", "Grateful Dead", "Dick's Picks Vol. 8: 1970-05-02");
        log.WriteLine($"date={t.ShowDate} via {t.DateFrom} era={t.EraId}");
        Assert.Equal(new DateOnly(1970, 5, 2), t.ShowDate);
        Assert.Equal(1, t.EraId);
    }

    // ------------------------------------------------------------- era map

    [Theory]
    [InlineData(1966, 0)]
    [InlineData(1969, 1)]
    [InlineData(1972, 2)]
    [InlineData(1977, 3)]
    [InlineData(1983, 4)]
    [InlineData(1989, 5)]
    [InlineData(1993, 6)]
    public void Era_boundaries_match_the_plan(int year, int era)
        => Assert.Equal(era, EraMap.FromDate(new DateOnly(year, 6, 1)));

    /// <summary>
    /// A date outside every era must be `unknown`, not clamped. Clamping would put a Dead &amp;
    /// Company show in "Final Years", which is exactly the kind of wrong a listener notices.
    /// </summary>
    [Theory]
    [InlineData(1964)]
    [InlineData(1996)]
    [InlineData(2015)]
    public void Dates_outside_every_era_are_unknown_not_clamped(int year)
        => Assert.Equal(EraMap.Unknown, EraMap.FromDate(new DateOnly(year, 6, 1)));

    [Fact]
    public void No_date_is_unknown() => Assert.Equal(EraMap.Unknown, EraMap.FromDate(null));

    // -------------------------------------------------------------- caption

    [Fact]
    public void Caption_reads_like_the_plans_example()
    {
        var p = Make();
        var t = p.Parse("Dark Star", "Grateful Dead",
                        "Sunshine Daydream (Live at Veneta, OR 8/27/72)");
        log.WriteLine(t.Caption);
        // §8.5: "Dark Star · Veneta, OR · 8/27/72"
        Assert.StartsWith("Dark Star ·", t.Caption);
        Assert.Contains("Veneta", t.Caption);
        Assert.EndsWith("8/27/72", t.Caption);
    }

    /// <summary>
    /// §8.5's actual requirement is LEGIBILITY FROM THREE FEET, which is a length constraint
    /// before it is a typography one. Built from the raw title this came out at 140 characters
    /// with the venue and the date in it twice.
    /// </summary>
    [Fact]
    public void Caption_stays_short_on_a_verbose_live_title()
    {
        var p = Make();
        var t = p.Parse(
            "Dark Star - Live at Old Renaissance Faire Grounds, Veneta, OR, 8/27/72",
            "Grateful Dead",
            "Sunshine Daydream (Live at Veneta, OR 8/27/72)");

        log.WriteLine($"{t.Caption.Length} chars: {t.Caption}");
        Assert.StartsWith("Dark Star ·", t.Caption);
        Assert.EndsWith("8/27/72", t.Caption);
        Assert.True(t.Caption.Length <= 60, $"caption is {t.Caption.Length} chars: {t.Caption}");
        // The venue and the date each appear once.
        Assert.Equal(1, t.Caption.Split("8/27/72").Length - 1);
        Assert.Equal(1, t.Caption.Split("Veneta").Length - 1);
    }

    [Theory]
    [InlineData("Barton Hall, Cornell University, Ithaca NY", "Ithaca, NY")]
    [InlineData("Old Renaissance Faire Grounds, Veneta OR", "Veneta, OR")]
    [InlineData("Winterland, San Francisco CA", "San Francisco, CA")]
    [InlineData("Great American Music Hall, San Francisco CA — 8/13/75", "San Francisco, CA")]
    [InlineData("Europe 1972", "Europe 1972")]
    public void Venue_shortens_to_the_place(string full, string want)
    {
        string got = TrackParser.ShortVenue(full);
        log.WriteLine($"{full,-52} -> {got}");
        Assert.Equal(want, got);
    }

    /// <summary>
    /// The three tracks that actually came through SMTC during Phase 3 verification, with the
    /// exact strings Spotify served. These are no longer hypothetical fixtures — each one has a
    /// 60 s capture beside it in tests/fixtures/real/auto/, so this test and that data describe
    /// the same three minutes of listening.
    ///
    /// Two of the three are the artist gate doing its job on material nobody chose to test it
    /// with: an Eagles remaster and a Vince Gill record both arrived unannounced and both were
    /// correctly refused.
    /// </summary>
    [Theory]
    [InlineData("Sugar Magnolia - Live at Merriweather Post Pavilion, Columbia, MD, 6/30/1985",
                "Grateful Dead", "Merriweather 6/30/85 (Live)",
                true, 4, "sugar magnolia")]
    // The SAME SONG thirteen years earlier, resolving to a different era purely from the date
    // in its album title. Nobody chose this pair — both arrived from ordinary listening — and
    // between them they are the clearest evidence the era map does what §9.3 says.
    [InlineData("Sugar Magnolia - Live in Veneta, Oregon 8/27/72",
                "Grateful Dead", "Veneta, OR 8/27/72: The Complete Sunshine Daydream Concert (Live)",
                true, 2, "sugar magnolia")]
    [InlineData("Don't Let Our Love Start Slippin' Away",
                "Vince Gill", "I Still Believe In You",
                false, EraMap.Unknown, "")]
    [InlineData("Take It Easy - 2013 Remaster",
                "Eagles", "Eagles (2013 Remaster)",
                false, EraMap.Unknown, "")]
    public void Tracks_captured_from_live_smtc_resolve_as_expected(
        string title, string artist, string album,
        bool family, int era, string songKey)
    {
        var p = Make();
        var t = p.Parse(title, artist, album);

        log.WriteLine($"{artist} — {title}");
        log.WriteLine($"    family {t.IsFamily}  era {t.EraId}  date {t.ShowDate?.ToString() ?? "none"}  "
            + $"song '{t.SongKey}'");
        log.WriteLine($"    caption: {t.Caption}");

        Assert.Equal(family, t.IsFamily);
        Assert.Equal(era, t.EraId);
        Assert.Equal(songKey, t.SongKey);

        // The dated live release is the one that has to carry a real show date — and, now that
        // 6/30/85 is in the venue book, a venue too. It reached the caption as date-only until
        // that row was added, which is the degradation working rather than a bug: an unknown
        // date is supposed to lose the venue and keep the date.
        if (family)
        {
            // Both dated live releases carry a real show date AND a venue. The 1985 row needed
            // venues.json extending — with a show the capture itself named, which is the data
            // file doing exactly what §9.2 keeps it in data for. The 1972 row needed nothing:
            // 8/27/72 was already there, being §8.5's own worked example.
            Assert.NotNull(t.ShowDate);
            Assert.NotNull(t.Venue);
            Assert.Contains("/", t.Caption);          // a day-precision date reached the caption
        }
        else
        {
            Assert.Null(t.ShowDate);
            Assert.Null(t.Venue);
        }

        // Every caption must be short enough to read from three feet, §8.5's actual ask.
        Assert.True(t.Caption.Length <= 60, $"caption is {t.Caption.Length} chars");
    }

    [Fact]
    public void Caption_is_empty_when_there_is_no_track()
    {
        var p = Make();
        Assert.Equal("", p.Parse("", "", "").Caption);
    }

    /// <summary>Nothing in the parser may throw, whatever it is handed.</summary>
    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", "", "")]
    [InlineData(">", "Grateful Dead", "")]
    [InlineData("   ", "   ", "   ")]
    [InlineData("Pt. 1", "Grateful Dead", "Pt. 2")]
    [InlineData("////", "Grateful Dead", "1977-13-45")]
    public void Parser_never_throws(string? title, string? artist, string? album)
    {
        var p = Make();
        var t = p.Parse(title, artist, album);
        log.WriteLine($"'{title}' / '{artist}' / '{album}' -> era {t.EraId}, key '{t.SongKey}'");
        Assert.InRange(t.EraId, 0, 7);
    }

    [Fact]
    public void Venue_book_loaded_from_data()
    {
        var b = VenueBook.Load(FindData());
        log.WriteLine($"{b.DateCount} dated shows, {b.SeriesCount} series entries");
        Assert.True(b.DateCount > 0, "venues.json byDate did not load");
        Assert.True(b.SeriesCount > 0, "venues.json series tables did not load");
    }

    /// <summary>A missing data file must give an empty book, not an exception.</summary>
    [Fact]
    public void Venue_book_tolerates_a_missing_file()
    {
        var b = VenueBook.Load(Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}"));
        Assert.Equal(0, b.DateCount);
        Assert.Null(b.VenueForDate(new DateOnly(1977, 5, 8)));
    }
}
