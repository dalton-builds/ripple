using Ripple.Audio;

namespace Ripple.Meta;

/// <summary>
/// Real-audio fixtures as a BYPRODUCT of normal use, rather than as a ritual.
///
/// The five fixtures §14 depends on blocked three development cycles for one reason: capturing
/// one is a separate deliberate act — stop what you are doing, run a wrapper, play the right
/// thing for sixty seconds, check the take. Nobody does that spontaneously, and so it never
/// happened, and the tempo octave and the per-band gamma stayed unresolvable for want of
/// material that normal listening would have produced by the dozen.
///
/// Phase 3 makes it free. Testing SMTC requires playing Spotify anyway, so every track change
/// while playing is already a capture opportunity: record 60 s, name it from the metadata, and
/// move on. The fixtures accumulate from listening.
///
/// FOUR RULES KEEP IT FROM BEING A NUISANCE:
///   - never capture the same track twice (the slug is the identity, and it includes album);
///   - never exceed a total disk cap, and stop cleanly rather than pruning someone's fixtures;
///   - only while actually playing, so a paused track does not record silence;
///   - never let a capture failure touch rendering.
/// </summary>
public sealed class AutoCapture(AudioEngine audio, string root, double seconds, long capBytes)
{
    private readonly AudioEngine _audio = audio;
    private readonly string _root = root;
    private readonly double _seconds = seconds;
    private readonly long _capBytes = capBytes;

    private FeatureRecorder? _recorder;
    private TrackInfo? _capturing;
    private long _startedTicks;

    /// <summary>Captures written this session.</summary>
    public int Written { get; private set; }

    /// <summary>Why the last capture did not start, for the HUD. Empty when idle or fine.</summary>
    public string LastSkip { get; private set; } = "";

    public bool Busy => _recorder is not null;

    /// <summary>What the HUD shows.</summary>
    public string Status => _recorder is not null && _capturing is not null
        ? $"capturing {_recorder.Seconds:0}/{_seconds:0}s — {_capturing.Title}"
        : Written > 0 ? $"{Written} written this session" : LastSkip.Length > 0 ? LastSkip : "idle";

    /// <summary>
    /// A track started. Begin a capture unless there is a reason not to.
    ///
    /// Called from the metadata thread. Everything it touches is owned by this object or is
    /// the audio engine's thread-safe recorder slot, so it does not marshal to the renderer.
    /// </summary>
    public void OnTrack(TrackInfo track, PlaybackState state)
    {
        if (_recorder is not null) return;                 // one at a time
        if (state != PlaybackState.Playing) { LastSkip = "not playing"; return; }
        if (track.Slug.Length == 0) { LastSkip = "no track identity"; return; }

        try
        {
            Directory.CreateDirectory(_root);

            string path = Path.Combine(_root, track.Slug + ".json");
            if (File.Exists(path)) { LastSkip = $"already captured: {track.Slug}"; return; }

            long used = DirectorySize(_root);
            if (used >= _capBytes)
            {
                // Stop, do not prune. These are regression fixtures and deleting someone's
                // baseline to make room for a new one is the opposite of what they are for.
                LastSkip = $"disk cap reached ({used / (1024 * 1024)} MB) — "
                    + "raise autoCaptureCapMb or clear the folder";
                return;
            }

            var fmt = _audio.Format ?? new AudioFormat(48_000, 2);
            _recorder = new FeatureRecorder(fmt.FrameRate, _seconds);
            _capturing = track;
            _startedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            _audio.AttachRecorder(_recorder);
            LastSkip = "";
            Console.WriteLine($"[capture] {_seconds:0}s -> {track.Slug}.json  ({track})");
        }
        catch (Exception ex)
        {
            Abort($"could not start: {ex.Message}");
        }
    }

    /// <summary>Playback stopped or the track changed mid-capture. Abandon it.</summary>
    public void OnInterrupted(string why)
    {
        if (_recorder is null) return;
        // A partial capture is worse than none: it would sit in the fixture folder looking
        // like a valid baseline while being twelve seconds of one chorus.
        Abort(why);
    }

    /// <summary>
    /// Called from the render loop. Writes the fixture when the recorder fills, or gives up
    /// if it has been far too long — a capture that never fills means audio stopped arriving,
    /// and waiting forever would block every subsequent track.
    /// </summary>
    public void Poll()
    {
        if (_recorder is null || _capturing is null) return;

        if (_recorder.Full) { Finish(); return; }

        double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_startedTicks).TotalSeconds;
        if (elapsed > _seconds * 2.5 + 10) Abort("timed out waiting for audio");
    }

    private void Finish()
    {
        var rec = _recorder!;
        var track = _capturing!;
        try
        {
            var fmt = _audio.Format ?? new AudioFormat(48_000, 2);
            string path = Path.Combine(_root, track.Slug + ".json");

            // The label is what makes a fixture self-describing six months later — the
            // filename says which capture it is, the label says what it was OF.
            string label = $"{track.Artist} — {track.Title}"
                + (track.Album.Length > 0 ? $" [{track.Album}]" : "")
                + (track.ShowDate is { } d ? $" · {d:yyyy-MM-dd}" : "")
                + $" · era {track.EraId}";

            rec.Write(path,
                $"Auto-captured from {_audio.DeviceName} via WASAPI loopback on track change, "
                + "one row per analysis hop.",
                fmt, _audio.DeviceName, label);

            Written++;
            Console.WriteLine($"[capture] wrote {rec.Count} frames ({rec.Seconds:0.#}s) "
                + $"-> {track.Slug}.json");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[capture] write failed: {ex.Message}");
        }
        finally
        {
            Detach();
        }
    }

    private void Abort(string why)
    {
        Console.WriteLine($"[capture] abandoned: {why}");
        LastSkip = why;
        Detach();
    }

    private void Detach()
    {
        _audio.AttachRecorder(null);
        _recorder = null;
        _capturing = null;
    }

    private static long DirectorySize(string dir)
    {
        try
        {
            long total = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
                total += new FileInfo(f).Length;
            return total;
        }
        catch (Exception) { return 0; }
    }
}
