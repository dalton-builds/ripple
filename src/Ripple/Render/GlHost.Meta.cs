using Ripple.Meta;

namespace Ripple.Render;

/// <summary>
/// Phase 3's half of the host: the metadata source, §9.4's degradation chain, the now-playing
/// caption, and auto-capture.
///
/// All of it is optional at runtime and none of it can take rendering down. That is §9's
/// non-negotiable and it is enforced structurally rather than by care: every field here is
/// nullable, every entry point checks, and the metadata source itself is behind an interface
/// whose null implementation is a first-class citizen.
/// </summary>
public sealed partial class GlHost
{
    private MetadataDirector? _meta;
    private AutoCapture? _capture;
    private Caption? _caption;

    /// <summary>
    /// Set when SMTC says paused or stopped. §11's idle is RMS-based and takes 3 s of silence;
    /// this makes a pause instant, which is the difference between "it noticed" and "it
    /// reacted".
    /// </summary>
    private volatile bool _metaPaused;

    /// <summary>The preset the metadata layer wants, and the song mode to merge over it.</summary>
    private int _metaPresetIndex = -1;
    private string? _metaSongMode;

    // ------------------------------------------------------------ lifecycle

    private void StartMetadata()
    {
        _caption = new Caption(_gl);

        if (!_cfg.Metadata)
        {
            Console.WriteLine("[meta] disabled by config — running audio-only");
            return;
        }

        IMetadataSource source;
        try
        {
            source = new SmtcMetadataSource();
        }
        catch (Exception ex)
        {
            // Constructing the projection can throw on a machine that cannot host it.
            Console.Error.WriteLine($"[meta] SMTC unavailable ({ex.GetType().Name}) — audio-only");
            source = new NullMetadataSource();
        }

        _meta = new MetadataDirector(source, _presets, _opt.DataRoot);
        _meta.PlaybackChanged += OnPlaybackChanged;

        if (_cfg.AutoCapture && _audio is not null)
        {
            string root = Path.Combine(FindFixtureRoot(), "auto");
            _capture = new AutoCapture(_audio, root, _cfg.AutoCaptureSeconds,
                                       (long)_cfg.AutoCaptureCapMb * 1024 * 1024);
            _meta.TrackStarted += (t, s) => _capture.OnTrack(t, s);
            Console.WriteLine($"[capture] auto-capture on: {_cfg.AutoCaptureSeconds:0}s per new "
                + $"track -> {root} (cap {_cfg.AutoCaptureCapMb} MB)");
        }
        else if (_cfg.AutoCapture && _audio is null)
        {
            Console.WriteLine("[capture] auto-capture needs audio — not started");
        }

        _meta.Start();
        Console.WriteLine("[meta] SMTC watching for Spotify");
    }

    private void StopMetadata()
    {
        _meta?.Dispose();
        _meta = null;
        _caption?.Dispose();
        _caption = null;
        _capture = null;
    }

    /// <summary>
    /// Auto-captures land beside the hand-made fixtures so `timeline-stats.mjs` and the gating
    /// tools see them without a path argument. Resolved from the repo when running in a tree
    /// and from beside the exe otherwise, which is the same rule Program.ResolveRoots uses.
    /// </summary>
    private string FindFixtureRoot()
    {
        foreach (var b in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(b);
            for (int up = 0; up < 6 && dir is not null; up++, dir = dir.Parent)
            {
                var cand = Path.Combine(dir.FullName, "tests", "fixtures", "real");
                if (Directory.Exists(cand)) return cand;
            }
        }
        // Not in a tree: put them somewhere the user will find them.
        return Path.Combine(RippleConfig.DefaultDir, "captures");
    }

    // ---------------------------------------------------------------- frame

    /// <summary>
    /// Called once per frame from OnRender, BEFORE the feature frame is built — a track change
    /// may select a different preset, and that has to land before the frame is assembled.
    /// </summary>
    private void PollMetadata()
    {
        _capture?.Poll();

        if (_meta is null) return;
        if (!_meta.Poll()) return;

        var track = _meta.Current;
        if (track is null) return;

        ApplyTrack(track);
    }

    /// <summary>
    /// §9.4's chain, applied. The MERGE ORDER is CONTRACTS §4 and is the whole contract:
    /// era → song mode → user config → governor clamp, governor last and absolute.
    ///
    /// The first two happen here (PresetStore.Resolve merges the song mode recursively over
    /// the era preset). The third is the preset lock — a user who pressed L has said what they
    /// want and metadata does not overrule it. The fourth happens every frame in DrawFrame,
    /// after mapping, where it always has.
    /// </summary>
    private void ApplyTrack(TrackInfo track)
    {
        var (index, mode) = _meta!.Resolve(track, _presetIndex);

        Console.WriteLine($"[meta] {track}");
        Console.WriteLine($"[meta] -> {_meta.Tier}");

        if (_presetLocked)
        {
            // User config beats metadata. Still show the caption: the lock is about the LOOK,
            // and suppressing the track name would just make it seem broken.
            Console.WriteLine("[meta] preset locked — keeping the current look");
        }
        else if (index >= 0 && (index != _metaPresetIndex || mode != _metaSongMode))
        {
            _metaPresetIndex = index;
            _metaSongMode = mode;
            SelectPreset(index, mode);
        }

        if (_cfg.Caption && track.Caption.Length > 0) _caption?.Show(track.Caption);
    }

    private void OnPlaybackChanged(PlaybackState state)
    {
        _metaPaused = state is PlaybackState.Paused or PlaybackState.Stopped;

        // A pause mid-capture abandons it: sixty seconds of a track with a gap in the middle
        // is not a fixture, it is a trap for whoever trusts the filename.
        if (_metaPaused) _capture?.OnInterrupted("playback paused");
    }

    /// <summary>The HUD line for the metadata layer.</summary>
    private string MetaStatus()
    {
        if (_meta is null) return "meta off";
        var t = _meta.Current;
        return $"{_meta.SourceStatus} · {_meta.Tier}"
             + (t is not null && t.ShowDate is { } d ? $" · {d:yyyy-MM-dd}" : "")
             + (_capture is not null ? $" · capture: {_capture.Status}" : "");
    }
}
