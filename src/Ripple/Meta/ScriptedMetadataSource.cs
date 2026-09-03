namespace Ripple.Meta;

/// <summary>
/// A metadata source driven from the command line, so §9.4's degradation tiers can be
/// exercised without waiting for the right thing to come up on shuffle.
///
/// THIS EXISTS BECAUSE TIER 2 WAS UNVERIFIABLE. Tier 1 (a dated show) and tier 3 (not the
/// family) both turn up within a few minutes of ordinary listening; tier 2 — recognised as
/// the family, no parseable date — needs a studio cut or an untitled bootleg, and across two
/// sessions of real playback one never arrived. Reporting a tier as "unexercised" twice in a
/// row is worse than building the fixture, and a scripted source is the fixture: the same code
/// path the real one drives, with the track text supplied rather than discovered.
///
/// It is a diagnostic, not a feature. `--meta-script` is undocumented in the tray menu and the
/// HUD says `scripted` so nobody mistakes a rehearsed caption for a real one.
/// </summary>
public sealed class ScriptedMetadataSource : IMetadataSource
{
    private readonly RawTrack[] _script;
    private readonly double _holdSeconds;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private int _index = -1;

    public bool Available => true;
    public string Status => $"scripted {Math.Max(_index, 0) + 1}/{_script.Length}";
    public RawTrack Current => _index >= 0 ? _script[_index] : default;

    public event Action<RawTrack>? TrackChanged;
    public event Action<PlaybackState>? PlaybackChanged;

    /// <param name="spec">
    /// Semicolon-separated tracks, each `Title|Artist|Album` with artist and album optional.
    /// Artist defaults to "Grateful Dead", because the point of the instrument is the tiers
    /// below the artist check rather than the artist check itself.
    /// </param>
    public ScriptedMetadataSource(string spec, double holdSeconds = 12.0)
    {
        _holdSeconds = holdSeconds;
        var list = new List<RawTrack>();
        foreach (string entry in spec.Split(';', StringSplitOptions.RemoveEmptyEntries
                                               | StringSplitOptions.TrimEntries))
        {
            string[] f = entry.Split('|');
            list.Add(new RawTrack(
                f.Length > 0 ? f[0].Trim() : "",
                f.Length > 1 && f[1].Trim().Length > 0 ? f[1].Trim() : "Grateful Dead",
                f.Length > 2 ? f[2].Trim() : "",
                PlaybackState.Playing));
        }

        // An empty script would advance forever past the end of the array; an empty RawTrack is
        // a legitimate thing to script (it is tier 3's no-metadata case) so this is not a throw.
        _script = list.Count > 0 ? list.ToArray() : [default];
    }

    public void Start()
    {
        Advance();
        PlaybackChanged?.Invoke(PlaybackState.Playing);
    }

    /// <summary>
    /// Called from the render thread. Advancing here rather than on a timer thread means the
    /// tier transition lands between frames, which is where a real one lands too.
    /// </summary>
    public void Poll()
    {
        if (_index >= _script.Length - 1) return;
        if (_clock.Elapsed.TotalSeconds < _holdSeconds) return;
        Advance();
    }

    private void Advance()
    {
        _index++;
        _clock.Restart();
        Console.WriteLine($"[meta] scripted {_index + 1}/{_script.Length}: "
            + $"\"{_script[_index].Title}\" / \"{_script[_index].Artist}\"");
        TrackChanged?.Invoke(_script[_index]);
    }

    public void Dispose() { }
}
