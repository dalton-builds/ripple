namespace Ripple.Meta;

/// <summary>
/// §9.3's date → era mapping, and nothing else. Seven dated eras plus "unknown".
///
/// The boundaries are the ones in the plan's table and they are deliberately hard edges: a
/// show is in an era or it is not, and a date that falls in a gap year lands in the era whose
/// range contains it rather than being interpolated. There is no such thing as half an era.
/// </summary>
public static class EraMap
{
    /// <summary>Era 7 — no date, no match, or an artist outside the family.</summary>
    public const int Unknown = 7;

    /// <summary>Inclusive year ranges, in era order. Index is the era id.</summary>
    private static readonly (int From, int To, string Name)[] Ranges =
    [
        (1965, 1967, "Primal"),
        (1968, 1970, "Live/Dead"),
        (1971, 1974, "Europe '72 / Wall of Sound"),
        (1975, 1979, "Terrapin"),
        (1980, 1986, "Brent"),
        (1987, 1990, "In the Dark"),
        (1991, 1995, "Final Years"),
    ];

    /// <summary>
    /// The era for a show date. A date OUTSIDE every range — a 1996 Furthur set, a 2015 Dead
    /// &amp; Company show — maps to <see cref="Unknown"/> rather than being clamped into the
    /// nearest era. Clamping would put a Dead &amp; Company show in "Final Years", which is
    /// wrong in the way that a listener would notice immediately.
    /// </summary>
    public static int FromDate(DateOnly? date)
    {
        if (date is not { } d) return Unknown;
        for (int i = 0; i < Ranges.Length; i++)
            if (d.Year >= Ranges[i].From && d.Year <= Ranges[i].To) return i;
        return Unknown;
    }

    /// <summary>The preset id an era maps to, matching the filenames in presets/eras/.</summary>
    public static string PresetId(int eraId) => eraId switch
    {
        0 => "era-primal",
        1 => "era-live-dead",
        2 => "era-europe72",
        3 => "era-terrapin",
        4 => "era-brent",
        5 => "era-in-the-dark",
        6 => "era-final",
        _ => "era-unknown",
    };

    public static string Name(int eraId)
        => eraId >= 0 && eraId < Ranges.Length ? Ranges[eraId].Name : "Unknown / Studio";
}
