using Ripple.Meta;
using Ripple.Presets;
using Xunit;

namespace Ripple.Tests.Meta;

/// <summary>
/// §9.4's three-tier degradation, which the plan calls the gate for the whole metadata layer.
///
/// WHY THIS FILE EXISTS. The tiers were built and only two of them were ever seen: tier 1
/// arrives with any archive show and tier 3 with anything that is not the family, but tier 2 —
/// recognised as the family with no parseable date — needs a studio cut, and across two
/// sessions of real playback one never came up. It was reported as "unexercised" twice. So the
/// chain is pinned here, through the REAL parser, the REAL venue book and the REAL preset
/// store rather than through stubs, because the interesting failures live in the data files:
/// a preset id that does not exist on disk resolves to a fallback index and looks like it
/// worked, and only loading the store catches it.
/// </summary>
public sealed class DegradationTierTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int up = 0; up < 8 && dir is not null; up++, dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "presets"))
             && Directory.Exists(Path.Combine(dir.FullName, "data")))
                return dir.FullName;
        throw new DirectoryNotFoundException(AppContext.BaseDirectory);
    }

    private static (MetadataDirector Meta, PresetStore Presets, TrackParser Parser) Rig()
    {
        string root = Root();
        var presets = new PresetStore(Path.Combine(root, "presets"), Path.Combine(root, "data"));
        var meta = new MetadataDirector(new NullMetadataSource(), presets,
                                        Path.Combine(root, "data"));
        var parser = new TrackParser(VenueBook.Load(Path.Combine(root, "data")));
        return (meta, presets, parser);
    }

    /// <summary>A sentinel no preset uses, so a fallback return is visible instead of plausible.</summary>
    private const int NoFallback = -999;

    // ------------------------------------------------------------------ tier 1

    [Fact]
    public void Tier_1_dated_show_resolves_to_its_era()
    {
        var (meta, presets, parser) = Rig();
        var t = parser.Parse("Dark Star", "Grateful Dead", "Sunshine Daydream (8/27/72)");

        Assert.True(t.IsFamily);
        Assert.NotNull(t.ShowDate);
        Assert.NotEqual(EraMap.Unknown, t.EraId);

        var (index, _) = meta.Resolve(t, NoFallback);
        Assert.Equal(presets.IndexOf(EraMap.PresetId(t.EraId)), index);
        Assert.NotEqual(NoFallback, index);
    }

    // ------------------------------------------------------------------ tier 2

    /// <summary>
    /// THE TIER THAT WAS NEVER EXERCISED. A studio cut: family, recognisable title, no date
    /// anywhere. It must land on the era-unknown preset and say so — not on whatever happened
    /// to be showing, which is what a bare `fallbackIndex` return would do.
    /// </summary>
    [Theory]
    [InlineData("Sugar Magnolia", "American Beauty")]
    [InlineData("Ripple", "American Beauty")]
    [InlineData("Box of Rain", "")]
    public void Tier_2_family_without_a_date_lands_on_era_unknown(string title, string album)
    {
        var (meta, presets, parser) = Rig();
        var t = parser.Parse(title, "Grateful Dead", album);

        Assert.True(t.IsFamily);
        Assert.Null(t.ShowDate);
        Assert.Equal(EraMap.Unknown, t.EraId);

        var (index, _) = meta.Resolve(t, NoFallback);

        int unknown = presets.IndexOf(EraMap.PresetId(EraMap.Unknown));
        Assert.True(unknown >= 0,
            $"presets/ has no '{EraMap.PresetId(EraMap.Unknown)}' — tier 2 has nowhere to land");
        Assert.Equal(unknown, index);
        Assert.NotEqual(NoFallback, index);
    }

    /// <summary>
    /// A dateless track whose title IS in songs.json still gets its song mode. The mode is a
    /// partial override, so tier 2 is where it merges over era-unknown rather than over an era.
    /// </summary>
    [Fact]
    public void Tier_2_still_applies_a_song_mode_when_the_title_is_known()
    {
        var (meta, presets, parser) = Rig();

        string? found = null;
        foreach (string key in presets.SongModes.Keys)
        {
            var probe = parser.Parse(key.Replace('-', ' '), "Grateful Dead", "");
            if (probe.ShowDate is null
             && TrackParser.MatchSongMode(probe, presets.SongModes.ContainsKey) is { } m)
            {
                found = m;
                break;
            }
        }
        // songs.json is non-empty and its keys are reachable from a plain title.
        Assert.NotNull(found);

        var t = parser.Parse(found!.Replace('-', ' '), "Grateful Dead", "");
        var (index, mode) = meta.Resolve(t, NoFallback);

        Assert.Equal(found, mode);
        Assert.Equal(presets.IndexOf(EraMap.PresetId(EraMap.Unknown)), index);
        Assert.Contains("song mode", meta.Tier);
    }

    /// <summary>
    /// The caption at tier 2, which is what a viewer actually sees the degradation as. §8.5's
    /// line is `title · artist · venue · date`; with no date and no venue the last two fields
    /// are simply absent, and what has to hold is that their absence leaves no empty segment
    /// and no dangling separator. That is a real hazard here because the line is assembled by
    /// joining a list — an unconditional `parts.Add` of a null venue would print " ·  · ".
    /// </summary>
    [Fact]
    public void Tier_2_caption_drops_the_venue_and_date_fields_cleanly()
    {
        var (_, _, parser) = Rig();
        var t = parser.Parse("Sugar Magnolia", "Grateful Dead", "American Beauty");

        Assert.Equal("Sugar Magnolia · Grateful Dead", t.Caption);

        var fields = t.Caption.Split('·');
        Assert.Equal(2, fields.Length);
        Assert.All(fields, f => Assert.NotEqual("", f.Trim()));
        Assert.Equal(t.Caption.Trim(), t.Caption);

        // No date, in any format — §8.5 shows a day and a studio cut has none to show. A bare
        // year off the album would be a precision that is not there, so it must not appear.
        Assert.DoesNotContain("/", t.Caption);
        Assert.DoesNotContain("1970", t.Caption);
    }

    /// <summary>
    /// Tier 1's caption for contrast, so the difference a viewer sees is pinned rather than
    /// assumed. This also records a departure from §8.5's example, which shows three fields
    /// (`Dark Star · Veneta, OR · 8/27/72`) and no artist: the artist is carried because the
    /// caption is the answer to "what is this", and on a bootleg the band name is the part a
    /// listener cannot infer from the title.
    /// </summary>
    [Fact]
    public void Tier_1_caption_carries_the_venue_and_the_day()
    {
        var (_, _, parser) = Rig();
        var t = parser.Parse("Dark Star", "Grateful Dead", "Sunshine Daydream (8/27/72)");

        Assert.StartsWith("Dark Star · Grateful Dead", t.Caption);
        Assert.Contains("8/27/72", t.Caption);
        Assert.All(t.Caption.Split('·'), f => Assert.NotEqual("", f.Trim()));
    }

    // ------------------------------------------------------------------ tier 3

    [Fact]
    public void Tier_3_not_the_family_is_audio_only_even_with_a_date_in_the_album()
    {
        var (meta, presets, parser) = Rig();
        var t = parser.Parse("Ripple", "Some Other Band", "Live 8/27/72");

        Assert.False(t.IsFamily);
        Assert.Equal(EraMap.Unknown, t.EraId);

        var (index, mode) = meta.Resolve(t, NoFallback);
        Assert.Null(mode);
        Assert.Equal(presets.IndexOf(EraMap.PresetId(EraMap.Unknown)), index);
        Assert.Contains("audio-only", meta.Tier);
    }

    [Fact]
    public void Tier_3_no_metadata_at_all_is_audio_only()
    {
        var (meta, presets, _) = Rig();

        var (index, mode) = meta.Resolve(null, NoFallback);
        Assert.Null(mode);
        Assert.Equal(presets.IndexOf(EraMap.PresetId(EraMap.Unknown)), index);
        Assert.Contains("no metadata", meta.Tier);
    }

    // ------------------------------------------------------------------ the chain

    /// <summary>
    /// Walking down the tiers must change what the layer reports at every step. If two tiers
    /// are indistinguishable the degradation is invisible, which is the failure the plan's
    /// "each tier has to look intentional on its own" clause is about.
    /// </summary>
    [Fact]
    public void The_three_tiers_are_visibly_different()
    {
        var (meta, _, parser) = Rig();

        var t1 = parser.Parse("Dark Star", "Grateful Dead", "Sunshine Daydream (8/27/72)");
        var t2 = parser.Parse("Sugar Magnolia", "Grateful Dead", "American Beauty");
        var t3 = parser.Parse("Ripple", "Some Other Band", "");

        var r1 = meta.Resolve(t1, NoFallback); string tier1 = meta.Tier;
        var r2 = meta.Resolve(t2, NoFallback); string tier2 = meta.Tier;
        var r3 = meta.Resolve(t3, NoFallback); string tier3 = meta.Tier;

        Assert.NotEqual(r1, r2);
        Assert.NotEqual(tier1, tier2);
        Assert.NotEqual(tier2, tier3);

        // Tiers 2 and 3 share the era-unknown PRESET by design — a dateless Dead song and a
        // non-Dead track both have no era to show. What separates them is the song mode and
        // the caption, so the preset index alone is deliberately not asserted different.
        Assert.Equal(r2.PresetIndex, r3.PresetIndex);
    }

    /// <summary>
    /// A user who pressed L outranks metadata (CONTRACTS §4: user config beats era and song
    /// mode). The director does not know about the lock — the host applies it afterwards — so
    /// what is pinned here is that Resolve stays pure, which is what makes applying an
    /// override after the fact safe.
    /// </summary>
    [Fact]
    public void Resolve_is_pure_and_repeatable()
    {
        var (meta, _, parser) = Rig();
        var t = parser.Parse("Sugar Magnolia", "Grateful Dead", "American Beauty");

        var a = meta.Resolve(t, NoFallback);
        string tierA = meta.Tier;
        var b = meta.Resolve(t, NoFallback);

        Assert.Equal(a, b);
        Assert.Equal(tierA, meta.Tier);
    }
}
