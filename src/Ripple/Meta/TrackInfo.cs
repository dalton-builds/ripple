namespace Ripple.Meta;

/// <summary>
/// What the metadata layer knows about the current track, after parsing.
///
/// Every field past <see cref="Title"/> is allowed to be absent, and the whole record is
/// allowed to be absent — §9.4's degradation chain is song mode → era → audio-only, and each
/// step has to work alone. Nothing here may be required for a frame to render.
/// </summary>
public sealed record TrackInfo
{
    /// <summary>Raw SMTC fields, before any normalization.</summary>
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";

    /// <summary>
    /// True when the artist is in the extended family (§9.2's artist gate). False means
    /// generic mode: era stays <see cref="EraMap.Unknown"/> and no song mode is applied, no
    /// matter what the title happens to look like.
    ///
    /// This gate is what stops a non-Dead track called "Ripple" or a jazz record from 1972
    /// being dragged into an era treatment.
    /// </summary>
    public bool IsFamily { get; init; }

    /// <summary>The show date, when one parsed. Null is the common case for studio albums.</summary>
    public DateOnly? ShowDate { get; init; }

    /// <summary>
    /// How the date was arrived at, for the diagnostics and for the tests. A date that came
    /// from a series keyword is worth telling apart from one parsed out of the album title.
    /// </summary>
    public DateSource DateFrom { get; init; }

    /// <summary>Caption venue text, from data/venues.json. Null degrades to date-only.</summary>
    public string? Venue { get; init; }

    /// <summary>Release series the album matched, if any — "dick's picks", "cornell".</summary>
    public string? Series { get; init; }

    /// <summary>Title with §9.2's noise stripped, lowercased — the song-mode lookup key.</summary>
    public string SongKey { get; init; } = "";

    /// <summary>
    /// Every song in a segue title, in order. "China Cat Sunflower > I Know You Rider" gives
    /// both; the first drives the preset and the second is what a crossfade would move to.
    /// </summary>
    public string[] SegueSongs { get; init; } = [];

    public bool IsSegue => SegueSongs.Length > 1;

    /// <summary>0-7, per §9.3. Falls back to <see cref="EraMap.Unknown"/>.</summary>
    public int EraId { get; init; } = EraMap.Unknown;

    /// <summary>
    /// The caption line, already assembled: "Dark Star · Veneta, OR · 8/27/72". Empty when
    /// there is nothing worth saying, which is not the same as an empty track.
    /// </summary>
    public string Caption { get; init; } = "";

    /// <summary>A stable, filesystem-safe identity for this track, for auto-capture dedupe.</summary>
    public string Slug { get; init; } = "";

    public override string ToString()
        => $"{Artist} — {Title}" + (ShowDate is { } d ? $" [{d:yyyy-MM-dd}]" : "")
         + $" era {EraId}" + (SongKey.Length > 0 ? $" song '{SongKey}'" : "");
}

/// <summary>Where a parsed date came from. <see cref="None"/> means no date was found.</summary>
public enum DateSource
{
    None = 0,
    /// <summary>1977-05-08 style, in the album or title.</summary>
    Iso,
    /// <summary>5/8/77 style.</summary>
    Numeric,
    /// <summary>Europe '72 — a two-digit year only, so the day is unknown.</summary>
    YearOnly,
    /// <summary>A bare four-digit year.</summary>
    BareYear,
    /// <summary>Matched a release-series keyword whose date is known but not in the string.</summary>
    Series,
}
