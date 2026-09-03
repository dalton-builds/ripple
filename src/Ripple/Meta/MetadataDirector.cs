using Ripple.Presets;

namespace Ripple.Meta;

/// <summary>
/// The glue: metadata source → parser → which preset the renderer should be showing.
///
/// It owns §9.4's degradation chain, and the chain is the whole point rather than a fallback
/// path. Three tiers, each of which has to look intentional on its own:
///
///   SONG MODE   a known title in data/songs.json, merged over its era
///   ERA         a parsed date, mapped to one of presets/eras/
///   AUDIO-ONLY  neither — era-unknown, driven entirely by the DSP
///
/// The merge order is CONTRACTS §4 and is not negotiable: era → song mode → user config →
/// governor clamp, with the governor last and absolute. This class produces the first two; the
/// host applies the rest, in that order, every frame.
///
/// Everything here tolerates an absent source. `IMetadataSource` may be the null one, the
/// parser may find nothing, and the result is a valid state the renderer draws normally.
/// </summary>
public sealed class MetadataDirector : IDisposable
{
    private readonly IMetadataSource _source;
    private readonly TrackParser _parser;
    private readonly PresetStore _presets;

    /// <summary>Latched by the metadata thread, read by the renderer.</summary>
    private volatile TrackInfo? _pending;
    private readonly object _lock = new();

    /// <summary>The track currently being shown. Null until one arrives.</summary>
    public TrackInfo? Current { get; private set; }

    /// <summary>Playback state, for the pause-to-idle path.</summary>
    public PlaybackState State { get; private set; } = PlaybackState.Unknown;

    /// <summary>Which tier the current track resolved to, for the HUD and the gate.</summary>
    public string Tier { get; private set; } = "audio-only";

    /// <summary>The song-mode key applied, or null.</summary>
    public string? SongMode { get; private set; }

    /// <summary>Raised on the render thread when the resolved track changes.</summary>
    public event Action<TrackInfo>? TrackResolved;

    /// <summary>Raised on the metadata thread. Auto-capture hangs off this.</summary>
    public event Action<TrackInfo, PlaybackState>? TrackStarted;

    /// <summary>Raised on the metadata thread when play/pause/stop changes.</summary>
    public event Action<PlaybackState>? PlaybackChanged;

    public bool Available => _source.Available;
    public string SourceStatus => _source.Status;

    public MetadataDirector(IMetadataSource source, PresetStore presets, string dataRoot)
    {
        _source = source;
        _presets = presets;
        _parser = new TrackParser(VenueBook.Load(dataRoot));

        source.TrackChanged += OnRawTrack;
        source.PlaybackChanged += s =>
        {
            State = s;
            PlaybackChanged?.Invoke(s);
        };
    }

    public void Start() => _source.Start();

    private void OnRawTrack(RawTrack raw)
    {
        TrackInfo info;
        try
        {
            info = _parser.Parse(raw.Title, raw.Artist, raw.Album);
        }
        catch (Exception ex)
        {
            // The parser is written not to throw, so this is belt and braces — but §9's
            // non-negotiable is that metadata failure never takes down rendering, and a
            // regex that surprises us must not be the exception to it.
            Console.Error.WriteLine($"[meta] parse failed: {ex.Message}");
            return;
        }

        lock (_lock) _pending = info;
        TrackStarted?.Invoke(info, raw.State);
    }

    /// <summary>
    /// Called from the render loop. Returns true when the track changed, in which case the
    /// caller should re-select its preset.
    /// </summary>
    public bool Poll()
    {
        _source.Poll();

        TrackInfo? next;
        lock (_lock) { next = _pending; _pending = null; }
        if (next is null) return false;

        Current = next;
        TrackResolved?.Invoke(next);
        return true;
    }

    /// <summary>
    /// §9.4's chain, resolved. Returns the preset index and the song mode to merge over it.
    ///
    /// The SONG MODE IS A PARTIAL OVERRIDE, not a replacement — `PresetStore.ResolveNode`
    /// recursively merges it over the era preset, so a mode that names only `feedback.decay`
    /// keeps every other authored value. That is what lets data/songs.json be 20 lines rather
    /// than 20 full presets.
    /// </summary>
    public (int PresetIndex, string? SongMode) Resolve(TrackInfo? track, int fallbackIndex)
    {
        if (track is null || !track.IsFamily)
        {
            Tier = track is null ? "audio-only (no metadata)" : "audio-only (not the family)";
            SongMode = null;
            // Not the family: era-unknown if we have it, else whatever was already showing.
            int unknown = _presets.IndexOf(EraMap.PresetId(EraMap.Unknown));
            return (unknown >= 0 ? unknown : fallbackIndex, null);
        }

        int eraIndex = _presets.IndexOf(EraMap.PresetId(track.EraId));
        if (eraIndex < 0) eraIndex = fallbackIndex;

        string? mode = null;
        try
        {
            mode = TrackParser.MatchSongMode(track, _presets.SongModes.ContainsKey);
        }
        catch (Exception) { mode = null; }

        SongMode = mode;
        Tier = mode is not null
            ? $"song mode '{mode}' over {EraMap.PresetId(track.EraId)}"
            : track.EraId == EraMap.Unknown
                ? "era-unknown (no date)"
                : $"era {track.EraId} ({EraMap.Name(track.EraId)})";

        return (eraIndex, mode);
    }

    public void Dispose() => _source.Dispose();
}
