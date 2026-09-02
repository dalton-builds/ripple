using System.Diagnostics;
using Ripple.Render;

namespace Ripple.Audio;

/// <summary>
/// Owns the capture, the ring, the analysis thread and the frame exchange — the whole audio
/// side behind one object the renderer can hold.
///
/// Plan §6's threading model, made concrete:
///
///   audio callback thread  (NAudio)  -> RingBuffer.Write, nothing else
///   analysis thread        (here)    -> Analyzer, then FrameExchange.Publish
///   render thread          (GlHost)  -> FrameExchange.TryRead, never blocks on audio
///
/// The render thread never touches the ring, never waits on a lock, and never calls into
/// NAudio. If audio stops entirely the renderer keeps running on the last published frame
/// and <see cref="Idle"/> goes true, which is what §11's idle handling keys off.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    /// <summary>RMS below this for <see cref="IdleSeconds"/> counts as silence (§11).</summary>
    private const double SilenceDbfs = -50;
    private const double IdleSeconds = 3.0;

    private readonly RingBuffer _ring;
    private readonly LoopbackCapture _capture;
    private readonly FrameExchange _exchange = new();

    private Analyzer? _analyzer;
    private FeatureRecorder? _recorder;
    private Thread? _analysis;
    private volatile bool _running;
    private readonly object _analyzerLock = new();

    /// <summary>Hops to accumulate before waking the DSP. See AnalysisLoop.</summary>
    private const int HopsPerWake = 3;

    private readonly float[] _readScratch = new float[AudioFormat.Hop * 16];

    // Cost measurement, for §11's "under 1% of one core" budget.
    private long _dspTicks;
    private long _dspSteps;
    private double _dspMsEma;
    private long _startTicks;

    private double _lastRms;
    private double _quietSeconds;

    public AudioEngine()
    {
        // Two seconds of headroom at 96 kHz stereo. Overrun means the analysis thread
        // stalled, which the dropped counter surfaces rather than hiding.
        _ring = new RingBuffer(96_000 * 2 * 2);
        _capture = new LoopbackCapture(_ring);
        _capture.FormatChanged += OnFormatChanged;
    }

    public FrameExchange Frames => _exchange;

    /// <summary>
    /// Record every published frame, at the ANALYSIS rate.
    ///
    /// This has to live on the analysis thread. Sampling the exchange from the render loop
    /// instead looks equivalent and is not: the renderer runs at whatever the GPU manages —
    /// 22 fps for this workload on an Intel UHD — while the DSP publishes at 93.8, so a
    /// render-driven recorder captured one row in four and still labelled the file 93.8 fps.
    /// Replaying that timeline ran four times too fast, and nothing about the file said so.
    /// </summary>
    public void AttachRecorder(FeatureRecorder? recorder) => _recorder = recorder;

    public FeatureRecorder? Recorder => _recorder;

    /// <summary>
    /// A readable summary of what the DSP currently thinks, for the diagnostic readout
    /// (§15's Phase 2 "diagnostic HUD", in console form).
    ///
    /// This exists because a capture with no feedback is a black box: you find out the
    /// tempo never locked, or the level was 30 dB too low, after the take rather than
    /// during it. Cheap enough to call a few times a second.
    /// </summary>
    public string Readout()
    {
        Analyzer? a;
        lock (_analyzerLock) a = _analyzer;
        if (a is null) return "no analyzer yet";

        var f = a.LastFrame.AsSpan();
        double dbfs = 20 * Math.Log10(a.RawRms + 1e-9);

        // Eight bands as a bar each, so a spectrally lopsided capture is obvious at a
        // glance rather than after a distribution analysis.
        const string ramp = " .:-=+*#%@";
        var bars = new char[8];
        for (int b = 0; b < 8; b++)
            bars[b] = ramp[Math.Clamp((int)(f[FF.Sub + b] * (ramp.Length - 1)), 0, ramp.Length - 1)];

        string key = KeyName(a.KeyPitchClass, a.KeyIsMinor);
        string tempo = a.BpmConfidence < Analyzer.ConfidenceFloor
            ? $"free-time (conf {a.BpmConfidence:0.00})"
            : $"{a.Bpm,5:0.0} BPM conf {a.BpmConfidence:0.00}";

        return $"{dbfs,6:0.0} dBFS  [{new string(bars)}]  {tempo}  key {key,-3} "
             + $"perc {f[FF.Percussivity]:0.00}  width {f[FF.StereoWidth]:0.00}";
    }

    private static string KeyName(int pc, bool minor)
    {
        string[] names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        return names[Math.Clamp(pc, 0, 11)] + (minor ? "m" : "");
    }

    /// <summary>Per-stage DSP cost from the live analyzer, for the §11 breakdown.</summary>
    public IEnumerable<(string Name, double MsPerHop)> StageCostMs()
    {
        lock (_analyzerLock)
            return _analyzer?.StageCostMs().ToList()
                   ?? Enumerable.Empty<(string, double)>();
    }
    public string Status => _capture.Status;
    public string DeviceName => _capture.DeviceName;
    public AudioFormat? Format => _capture.Format;
    public bool Healthy => _capture.Healthy;
    public long Restarts => _capture.Restarts;
    public long DroppedSamples => _ring.TotalDropped;
    public long AnalyzedFrames => _dspSteps;

    /// <summary>Mean DSP milliseconds per analysis hop. The §11 budget number.</summary>
    public double DspMsPerHop => _dspSteps > 0
        ? Stopwatch.GetElapsedTime(0, _dspTicks).TotalMilliseconds / _dspSteps
        : 0;

    /// <summary>Smoothed DSP cost, for the HUD.</summary>
    public double DspMsEma => _dspMsEma;

    /// <summary>
    /// DSP cost as a fraction of one core, measured over the engine's whole lifetime. This
    /// is the number §11 caps at 1%.
    /// </summary>
    public double DspCoreFraction
    {
        get
        {
            if (_startTicks == 0) return 0;
            double wall = Stopwatch.GetElapsedTime(_startTicks).TotalSeconds;
            double cpu = Stopwatch.GetElapsedTime(0, _dspTicks).TotalSeconds;
            return wall > 0 ? cpu / wall : 0;
        }
    }

    /// <summary>
    /// True when there is nothing to react to: the capture is unhealthy, or the signal has
    /// been below −50 dBFS for three seconds (§11).
    /// </summary>
    public bool Idle => !_capture.Healthy || _quietSeconds >= IdleSeconds;

    public double SecondsSinceAudio => _capture.SecondsSinceData;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _startTicks = Stopwatch.GetTimestamp();

        _capture.Start();

        // If the device could not be opened the analyzer is built on the first successful
        // FormatChanged instead; the analysis thread simply idles until then.
        if (_capture.Format is { } fmt) EnsureAnalyzer(fmt);

        _analysis = new Thread(AnalysisLoop)
        {
            IsBackground = true,
            Name = "ripple-analysis",
            // Above normal, below realtime: the DSP must not be starved by the render
            // thread, but it is not worth risking the rest of the machine for.
            Priority = ThreadPriority.AboveNormal,
        };
        _analysis.Start();
    }

    private void OnFormatChanged(AudioFormat fmt)
    {
        Console.WriteLine($"[audio] format is now {fmt}");
        EnsureAnalyzer(fmt);
    }

    private void EnsureAnalyzer(AudioFormat fmt)
    {
        lock (_analyzerLock)
        {
            if (_analyzer is not null && _analyzer.Format == fmt) return;
            // A new Analyzer rather than a reconfigure: every table in it is derived from
            // the sample rate, and rebuilding is a few microseconds on a device change.
            _analyzer = new Analyzer(fmt);
            _ring.Clear();
        }
    }

    /// <summary>
    /// The analysis thread. Wakes on available audio, runs the DSP one hop at a time, and
    /// publishes each completed frame.
    /// </summary>
    private void AnalysisLoop()
    {
        var frame = default(FeatureFrame);

        while (_running)
        {
            Analyzer? a;
            lock (_analyzerLock) a = _analyzer;

            // Wait for a BATCH of hops rather than for one.
            //
            // Waking every 4 ms meant ~250 wake-ups a second to do 94 hops of work: most
            // wakes found nothing, and each hop that did run started with a cold cache on a
            // core that had no reason to be boosted. Measured in the running app every
            // stage cost ~2.4x what the same code costs in a sustained loop — uniformly, so
            // it was per-wake overhead rather than any one stage.
            //
            // Batching trades latency for locality. Three hops is 32 ms of audio, and R1
            // allows 60 ms from transient to visible response, most of which the render
            // loop spends. The ring holds two seconds, so nothing is lost by waiting.
            if (a is null || _ring.Count < AudioFormat.Hop * HopsPerWake)
            {
                Thread.Sleep(4);
                continue;
            }

            int n = _ring.Read(_readScratch);
            if (n <= 0) continue;

            long t0 = Stopwatch.GetTimestamp();
            a.Append(_readScratch.AsSpan(0, n));

            while (a.TryStep(ref frame))
            {
                _exchange.Publish(frame);
                _recorder?.Add(frame);
                _dspSteps++;
                TrackSilence(frame, a.Format.HopSeconds);
            }

            long spent = Stopwatch.GetTimestamp() - t0;
            _dspTicks += spent;
            double ms = Stopwatch.GetElapsedTime(0, spent).TotalMilliseconds;
            _dspMsEma += (ms - _dspMsEma) * 0.05;
        }
    }

    /// <summary>§11's silence detector: RMS below −50 dBFS for three seconds is idle.</summary>
    private void TrackSilence(in FeatureFrame frame, double dt)
    {
        // The frame's rms field is adaptively normalized, so it cannot answer "is this
        // quiet in absolute terms" — that is the whole point of the normalization. Peak is
        // equally relative. So ask the analyzer's own pre-normalization level instead.
        double rms = _lastRms;
        lock (_analyzerLock) rms = _analyzer?.RawRms ?? 0;

        double dbfs = 20 * Math.Log10(rms + 1e-9);
        if (dbfs < SilenceDbfs) _quietSeconds += dt;
        else _quietSeconds = 0;
        _lastRms = rms;
    }

    /// <summary>Latest published frame, or false if none yet. Render thread.</summary>
    public bool TryReadFrame(ref FeatureFrame into) => _exchange.TryRead(ref into);

    public void Dispose()
    {
        _running = false;
        _analysis?.Join(500);
        _capture.FormatChanged -= OnFormatChanged;
        _capture.Dispose();
    }
}
