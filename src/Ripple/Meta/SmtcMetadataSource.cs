using System.Diagnostics;
using Windows.Media.Control;

namespace Ripple.Meta;

/// <summary>
/// §9.1 — Windows SMTC (`Windows.Media.Control`), filtered to Spotify.
///
/// Three things about this API make the shape of this class what it is:
///
/// IT IS ASYNC AND WinRT, and the render thread is neither. Every call here goes through
/// <see cref="Wait"/> with a timeout, on a background thread, and the result is latched into a
/// volatile field that <see cref="Poll"/> hands back. The render thread never awaits anything.
///
/// ITS EVENTS ARE OCCASIONALLY MISSED. The plan says so and it is true — a track change during
/// a session-list rebuild can produce no `MediaPropertiesChanged` at all. So the events are
/// treated as a latency optimisation over a 2 s poll, not as the mechanism. Everything works
/// with the events removed; they just make it feel instant.
///
/// IT CAN FAIL TO LOAD ENTIRELY. On a machine without the projection, or under a
/// desktop-bridge sandbox, constructing the manager throws. That must be indistinguishable
/// from "no music playing", which is why every path here ends in <see cref="Available"/> false
/// rather than an exception.
/// </summary>
public sealed class SmtcMetadataSource : IMetadataSource
{
    /// <summary>
    /// Matched as a SUBSTRING of SourceAppUserModelId, lowercased. The desktop build reports
    /// `Spotify.exe`; the Store build reports something like
    /// `SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify`. Both contain "spotify".
    /// </summary>
    private const string SpotifyMatch = "spotify";

    /// <summary>§9.1's safety net. Also the only mechanism if the events never fire.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Every WinRT await is bounded by this. An SMTC call that hangs must cost one poll, not
    /// the metadata thread.
    /// </summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(4);

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    private Thread? _thread;
    private volatile bool _running;
    private readonly ManualResetEventSlim _wake = new(false);

    private volatile string _status = "starting";
    private RawTrack _current;
    private readonly object _lock = new();

    /// <summary>
    /// True only when a Spotify session has actually been found. Set false again if it goes
    /// away — Spotify closing is not an error, it is just no metadata.
    /// </summary>
    public bool Available { get; private set; }

    public string Status => _status;

    public RawTrack Current { get { lock (_lock) return _current; } }

    /// <summary>Raised on the metadata thread when the track identity changes.</summary>
    public event Action<RawTrack>? TrackChanged;

    /// <summary>Raised on the metadata thread when play/pause/stop changes.</summary>
    public event Action<PlaybackState>? PlaybackChanged;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Pump) { IsBackground = true, Name = "ripple-smtc" };
        // WinRT projections want an apartment; MTA is correct for a worker that only awaits.
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Nothing to do — the metadata thread latches, this only reads.</summary>
    public void Poll() { }

    // ------------------------------------------------------------------ pump

    private void Pump()
    {
        try
        {
            _manager = Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
            if (_manager is null)
            {
                _status = "unavailable (no session manager)";
                Console.Error.WriteLine("[meta] SMTC session manager unavailable — "
                    + "running audio-only");
                return;
            }

            // Session list changes are how Spotify appears and disappears.
            _manager.SessionsChanged += (_, _) => _wake.Set();
            _status = "waiting for Spotify";

            while (_running)
            {
                try { Tick(); }
                catch (Exception ex)
                {
                    // One bad tick must not end the thread: SMTC throws transiently while a
                    // session is being torn down, and the next poll usually succeeds.
                    _status = $"error: {ex.Message}";
                }
                _wake.Wait(PollInterval);
                _wake.Reset();
            }
        }
        catch (Exception ex)
        {
            // Includes the projection failing to load at all.
            Available = false;
            _status = $"unavailable ({ex.GetType().Name})";
            Console.Error.WriteLine($"[meta] SMTC unavailable: {ex.Message} — running audio-only");
        }
    }

    private void Tick()
    {
        var session = FindSpotify();
        if (session is null)
        {
            if (Available)
            {
                Available = false;
                _status = "waiting for Spotify";
                lock (_lock) _current = default;
                PlaybackChanged?.Invoke(PlaybackState.Stopped);
            }
            return;
        }

        if (!ReferenceEquals(session, _session))
        {
            Detach();
            _session = session;
            // Events are a latency optimisation over the poll, nothing more — see the class
            // note. Both handlers just wake the loop rather than doing work on SMTC's thread.
            _session.MediaPropertiesChanged += (_, _) => _wake.Set();
            _session.PlaybackInfoChanged += (_, _) => _wake.Set();
            Available = true;
        }

        var props = Wait(_session.TryGetMediaPropertiesAsync());
        var info = _session.GetPlaybackInfo();

        var state = info?.PlaybackStatus switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PlaybackState.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PlaybackState.Paused,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => PlaybackState.Stopped,
            _ => PlaybackState.Unknown,
        };

        var next = new RawTrack(
            props?.Title ?? "", props?.Artist ?? "", props?.AlbumTitle ?? "", state);

        RawTrack prev;
        lock (_lock) { prev = _current; _current = next; }

        _status = state == PlaybackState.Playing
            ? $"Spotify · {next.Artist} — {next.Title}"
            : $"Spotify · {state.ToString().ToLowerInvariant()}";

        if (next.Key != prev.Key && next.HasTrack) TrackChanged?.Invoke(next);
        if (next.State != prev.State) PlaybackChanged?.Invoke(next.State);
    }

    private GlobalSystemMediaTransportControlsSession? FindSpotify()
    {
        if (_manager is null) return null;
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
        try { sessions = _manager.GetSessions(); }
        catch (Exception) { return null; }

        foreach (var s in sessions)
        {
            string id;
            try { id = s.SourceAppUserModelId ?? ""; }
            catch (Exception) { continue; }
            if (id.Contains(SpotifyMatch, StringComparison.OrdinalIgnoreCase)) return s;
        }
        return null;
    }

    private void Detach()
    {
        // The handlers are lambdas over _wake only, so letting the session go is enough; there
        // is no captured state to leak and no GL object to free.
        _session = null;
    }

    /// <summary>
    /// Await a WinRT operation with a timeout, from a non-async context. Returns default on
    /// timeout or failure — every caller treats that as "no metadata this tick".
    /// </summary>
    private static T? Wait<T>(Windows.Foundation.IAsyncOperation<T> op)
    {
        try
        {
            var task = op.AsTask();
            return task.Wait(CallTimeout) ? task.Result : default;
        }
        catch (Exception)
        {
            return default;
        }
    }

    public void Dispose()
    {
        _running = false;
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
        Detach();
        _manager = null;
        _wake.Dispose();
    }
}
