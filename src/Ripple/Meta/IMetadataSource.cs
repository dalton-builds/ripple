namespace Ripple.Meta;

/// <summary>Playback state, as far as the visualizer cares.</summary>
public enum PlaybackState { Unknown = 0, Playing, Paused, Stopped }

/// <summary>What a metadata source reports. All fields may be empty.</summary>
public readonly record struct RawTrack(string Title, string Artist, string Album,
                                       PlaybackState State)
{
    public bool HasTrack => Title.Length > 0 || Artist.Length > 0;

    /// <summary>Identity for change detection. Deliberately excludes playback state.</summary>
    public string Key => $"{Artist}␟{Title}␟{Album}";
}

/// <summary>
/// The seam that keeps §9's non-negotiable true: **metadata failure never takes down
/// rendering.** Everything downstream of this interface treats an absent source and an empty
/// track identically, and <see cref="NullMetadataSource"/> is a first-class implementation
/// rather than a test double — it is what runs when SMTC is unavailable, when the user turns
/// metadata off, and on any Windows where the projection fails to load.
///
/// Implementations must not throw from any member. A source that cannot work reports
/// <see cref="Available"/> false and keeps returning empty tracks.
/// </summary>
public interface IMetadataSource : IDisposable
{
    /// <summary>False when this source cannot produce metadata. Not an error condition.</summary>
    bool Available { get; }

    /// <summary>Human-readable state, for the diagnostic HUD.</summary>
    string Status { get; }

    /// <summary>The current track. Never throws; returns default when nothing is known.</summary>
    RawTrack Current { get; }

    /// <summary>
    /// Poll for changes. Called from the render thread, so it must not block — event-driven
    /// sources latch into a field and return it here.
    /// </summary>
    void Poll();

    /// <summary>Begin. Safe to call when unavailable; leaves <see cref="Available"/> false.</summary>
    void Start();
}

/// <summary>
/// No metadata, forever. The app runs fully audio-only on this: era stays
/// <see cref="EraMap.Unknown"/>, no song mode, no caption.
/// </summary>
public sealed class NullMetadataSource : IMetadataSource
{
    public bool Available => false;
    public string Status => "disabled";
    public RawTrack Current => default;
    public void Poll() { }
    public void Start() { }
    public void Dispose() { }
}
