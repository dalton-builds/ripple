using System.Diagnostics;
using Ripple.Render;

namespace Ripple.Audio;

/// <summary>
/// The DSP of plan §7: 8 perceptual bands with adaptive normalization, spectral-flux
/// onsets, autocorrelation tempo with a phase-locked beat accumulator, chroma and key,
/// stereo width, HPSS-lite percussivity, and structural novelty — in, a stream of
/// samples; out, a completed <see cref="FeatureFrame"/>.
///
/// Runs on the analysis thread. Everything is preallocated and span-based: no LINQ, no
/// allocation per hop, nothing that the Phase 4 zero-Gen0 assertion will object to.
///
/// Two properties matter more than the rest of the file, and both are about behaviour
/// rather than correctness:
///
/// 1. NORMALIZED RANGES MUST MATCH THE SYNTHETIC DRIVER. All 13 presets were authored
///    against synthetic features. If the real DSP's bands sit at 0.05 where the synthetic
///    driver gives 0.4, every preset looks wrong and it reads as a visual problem rather
///    than the scaling problem it is. NormalizationTests compares the two distributions.
///
/// 2. THE bpmConfidence &lt; 0.35 FALLBACK (§7.3). Below that, the phase accumulator
///    freezes and beat phase comes from the decaying onset impulse instead. This is what
///    makes Drums → Space work rather than the visuals insisting on a grid that is not
///    there. AnalyzerTests covers it explicitly.
/// </summary>
public sealed class Analyzer
{
    /* ------------------------------------------------------------ constants */

    /// <summary>Plan §7.1. Edges in Hz; bins are derived from the actual sample rate.</summary>
    private static readonly (double Lo, double Hi)[] BandEdges =
    [
        (20, 60),        // sub          global scale, feedback zoom
        (60, 150),       // bass         warp amplitude, kick response
        (150, 400),      // lowMid       fractal iteration depth
        (400, 1000),     // mid          vocal presence, motif opacity
        (1000, 2400),    // hiMid        edge detail, line weight
        (2400, 5000),    // presence     particle emission
        (5000, 10000),   // brilliance   bloom threshold
        (10000, 16000),  // air          grain, shimmer
    ];

    /// <summary>
    /// Scales the raw cosine distance so a section change reads near 1. See Novelty().
    /// </summary>
    private const double NoveltyGain = 2.5;

    private const double FluxLoHz = 40, FluxHiHz = 8000;
    private const double ChromaLoHz = 55, ChromaHiHz = 5000;

    private const int FluxHistory = 43;          // ~460 ms of flux, per §7.2
    private const double OnsetRefractory = 0.050;
    private const double OnsetDecay = 0.150;

    /// <summary>
    /// After an onset fires, the bar for the NEXT one is raised to a fraction of the flux
    /// peak that fired it, and decays back to the ordinary threshold with this time constant.
    ///
    /// WHY IT IS NEEDED. The 50 ms refractory is four times shorter than the 150 ms envelope
    /// decay, so a second detection 50-80 ms later re-arms the impulse before it has fallen
    /// anywhere. On dense material the flux peak has shoulders that clear the median-based
    /// threshold, and the detector walks up them: measured 13.5 onsets/s on MusicLike at 108
    /// BPM against 3.6 genuine event times — 3.75 detections per event — which held F_ONSET
    /// at mean 0.762 and left the bolt motif's `onset > 0.82` gate armed on 40% of frames.
    /// Motifs then fired on their own cooldown rather than on transients. Sparse material was
    /// never affected (click track 2.9/s against 2.0 expected), which is why the earlier
    /// falling-edge guard looked like it had fixed this.
    ///
    /// THE MULTIPLIER IS ABOVE 1 ON PURPOSE, and that is the whole trick. A gate set to some
    /// FRACTION of the previous peak cannot enforce any spacing at all between peaks of equal
    /// height: if the bar starts below the peak that armed it, an identical peak one hop later
    /// already clears it, and the only thing left limiting the rate is the refractory. Tried
    /// at 0.72 and measured — 13.5 onsets/s fell only to 10.6, still three times the real
    /// event rate.
    ///
    /// Set above 1, the bar starts ABOVE the peak that armed it and the minimum spacing
    /// between equal peaks becomes tau * ln(multiplier) — here 0.13 * ln(3.5) = 163 ms, so at
    /// most about 6 onsets/s. A genuinely STRONGER hit still breaks through early, which is
    /// what makes this a prominence test rather than a longer refractory: a peak twice the
    /// height of the last one needs only 0.13 * ln(1.75) = 73 ms.
    ///
    /// At 108 BPM the half-beat grid is 278 ms and the gate is down to 3.5 * exp(-278/130) =
    /// 0.41 of the previous peak by then, so a hat at a fraction of a kick's flux still fires.
    /// </summary>
    private const double OnsetGateTau = 0.130, OnsetGateMultiplier = 3.5;

    /// <summary>
    /// Envelope time constant for the F_FLUX field (not for onset detection, which still
    /// sees raw per-hop flux and must, or it would smear the peak it looks for).
    ///
    /// 55 ms is ~5 hops at 93.75 fps: enough to bridge the hops where the half-wave
    /// rectified difference is zero, short enough that a cymbal swell still reads as a rise
    /// rather than as a plateau.
    /// </summary>
    private const double FluxEnvTau = 0.055;
    /// <summary>
    /// §7.3 specifies 6 s of onset envelope. Together with the median filter that put the
    /// estimate far enough behind a drifting tempo to miss §14's ±2 BPM (measured 2.56 BPM
    /// at 0.32 BPM/s), so this is 4 s — still four full periods at the 60 BPM floor, which
    /// is what the autocorrelation actually needs.
    /// </summary>
    private const double TempoHistorySeconds = 4.0;

    /// <summary>
    /// Rise and fall time constants for the confidence estimate.
    ///
    /// Confidence gates §7.3's fallback, so it switches the visuals between a phase-locked
    /// grid and onset-driven pulses — and an unsmoothed estimate flickers between the two.
    /// A 4 s autocorrelation window over free-time material occasionally finds a spurious
    /// period, and on arrhythmic clicks that pushed the raw estimate above the 0.35
    /// threshold on 55% of frames.
    ///
    /// Asymmetric on purpose: slow to trust a newly found pulse, quicker to give one up.
    /// Insisting on a grid that is not there is the failure mode §7.3 exists to prevent.
    /// </summary>
    private const double ConfRiseTau = 1.5, ConfFallTau = 0.5;

    /// <summary>
    /// §7.3 specifies an 8 s median. Measured against §14's own acceptance case — a click
    /// track drifting ±8% over 60 s, which is 0.32 BPM/s — an 8 s median plus the 6 s
    /// autocorrelation window puts the estimate ~7 s behind the truth, a 3.16 BPM lag that
    /// fails the same section's "track within 2 BPM" requirement. The two numbers are in
    /// tension and the drift case is the one that matters on Dead material, so the median
    /// is 2 s. That is still ~190 estimates to take a median over — ample for rejecting
    /// the occasional wrong-octave frame, which is the job — and it brings the measured
    /// worst-case drift error to within budget. Full chain: a 4 s autocorrelation window
    /// contributes ~2 s of group delay and a 2 s median ~1 s, so ~3 s total, which at
    /// 0.32 BPM/s is under 1 BPM of systematic lag.
    /// </summary>
    private const double BpmMedianSeconds = 2.0;
    private const double MinBpm = 60, MaxBpm = 180;

    /// <summary>Below this, freeze the phase accumulator. Plan §7.3, not negotiable.</summary>
    public const double ConfidenceFloor = 0.35;

    /// <summary>
    /// How much stronger the on-beat comb has to be than the off-beat one before the octave
    /// logic doubles the raw ACF peak, and how far it has to fall back before the decision
    /// is given up again. See <see cref="BeatCombRatio"/>.
    ///
    /// Measured over the synthetic signal space in GateDiagnostics: metronomic click tracks
    /// score 1.00-1.02 (no alternation, nothing to disturb), and MusicLike — kick and bass
    /// on the beat, hats on the half beat — scores 1.40 at 80 BPM and 1.56 at 88.
    ///
    /// CAVEAT, and it is the honest state of this constant: those are synthetic signals. The
    /// recording the plan designates as the unambiguous diagnostic is `tight-backbeat`, and
    /// it has not been captured yet. Re-fit against it before treating this as settled.
    /// </summary>
    private const double CombAlternation = 1.35, CombRelease = 1.15;

    /// <summary>
    /// Smoothing time constant of the comb ratio, and the ACF peak below which the octave
    /// question is not asked at all. Both exist for reasons recorded at the use site.
    /// </summary>
    private const double CombTau = 1.5, CombMinPeak = 0.18;

    /// <summary>
    /// Whether the comb ratio may actually MOVE the octave. Currently false, and the
    /// reasoning is the substance of this constant.
    ///
    /// The comb test was built and validated against an onset envelope that was saturated by
    /// a detector bug — 13.5 onsets/s where the material had 3.6. That density is what gave
    /// its interleaved combs enough content to compare. Fixing the detector took the density
    /// away, and the test stopped working: on MusicLike at 88 BPM the on-beat and off-beat
    /// combs now score within 2% of each other, so the case it was written for is no longer
    /// caught.
    ///
    /// Worse, it stopped being harmless. Left active it fires spuriously mid-run on §14's
    /// drifting-click-track acceptance case, which has been correct for the whole project:
    /// measured a 64.3 BPM excursion at 52.4 s against a ±2 BPM requirement, a clean octave
    /// jump, with mean error over the graded window at 24.8 BPM. Fixed-tempo click tracks are
    /// unaffected, which is exactly why this was not caught by the per-tempo diagnostic and
    /// needed the drift test to surface.
    ///
    /// So the ACTUATOR is off and the INSTRUMENT stays on: CombRatio and CombSmooth are still
    /// computed and still exposed, because they are what a re-fit against a real capture will
    /// be judged on. A discriminator that cannot be calibrated must not be left in the
    /// decision path, but throwing away the measurement would mean deriving it a third time.
    ///
    /// To close this: record tests/fixtures/real/tight-backbeat.json, re-fit
    /// <see cref="CombAlternation"/> against it, confirm the drift case still passes, and set
    /// this true.
    /// </summary>
    private const bool CombDoublingEnabled = false;

    /// <summary>
    /// The perceptual tempo prior: centre and log-width.
    ///
    /// `P(lag) = exp(-0.5 * (ln(bpm/centre) / sigma)^2)`, multiplied into the ACF when
    /// choosing the lag. Sigma is in NATURAL LOG of BPM, so 0.30 puts ±1 sigma at
    /// 120·e^±0.30 = 89..162 BPM.
    ///
    /// See GateDiagnostics.Tempo_prior_sweep for the measured trade-off, and the README for
    /// the conclusion. The short version: the prior does what it is supposed to do and cannot
    /// be turned up far enough to fix the ambiguous cases without breaking metronomic ones,
    /// because a click track at 60 or 180 BPM is genuinely far from any sensible centre.
    /// </summary>
    public const double TempoPriorCentre = 120.0;

    /// <summary>
    /// Log-width of the prior. 0 DISABLES it, which is the committed default — see the class
    /// note above. Settable so GateDiagnostics can sweep it rather than requiring a rebuild
    /// per value; production never writes it.
    /// </summary>
    internal double PriorSigma { get; set; }

    /// <summary>Log-normal prior over BPM, evaluated at a lag. 1.0 when disabled.</summary>
    private double TempoPrior(int lag, double frameRate)
    {
        if (PriorSigma <= 0 || lag <= 0) return 1.0;
        double bpm = 60.0 * frameRate / lag;
        double z = Math.Log(bpm / TempoPriorCentre) / PriorSigma;
        return Math.Exp(-0.5 * z * z);
    }

    /// <summary>
    /// Above this the off-beat comb is empty rather than quiet, which means no subdivision
    /// — see the note in <see cref="BeatCombRatio"/>. A real beat-against-subdivision
    /// pattern measures 1.3-2.5; free-time material measures in the thousands.
    /// </summary>
    private const double CombSparseLimit = 6.0;

    /// <summary>
    /// How often the two expensive stages actually recompute, in hops.
    ///
    /// §11 budgets the whole analysis chain at under 1% of one core. Measured per stage in
    /// Release, running every stage every hop came to 0.209 ms/hop — 1.96%, nearly twice
    /// the budget — and two stages were 72% of it: HPSS-lite percussivity at 45% (two
    /// 17-wide medians across 427 bins) and the tempo autocorrelation at 27% (64 lags over
    /// a 375-frame history).
    ///
    /// Neither output changes fast enough to need a hop. Percussivity is smoothed by a
    /// 120 ms envelope before anyone sees it, so recomputing every 3 hops (32 ms) is well
    /// inside its own response time. The tempo estimate feeds a 2 s median and drives a
    /// phase accumulator that advances every hop regardless, so a BPM figure up to 43 ms
    /// stale changes nothing observable.
    ///
    /// This is decimation, not approximation: the values are identical to what every-hop
    /// computation would produce, just refreshed less often.
    /// </summary>
    private const int PercussivityEvery = 4, TempoEvery = 4;

    /// <summary>
    /// Evaluate the HPSS mask on every Nth bin. Percussivity is a single ratio of two sums
    /// over hundreds of bins, so a uniform half-sample shifts it by far less than the
    /// 120 ms envelope already does — and it halves what was still the most expensive stage
    /// in the chain. The spectrogram history is unaffected: only the mask evaluation is
    /// strided.
    /// </summary>
    private const int HpssBinStride = 2;

    /* -------------------------------------------------------------- plumbing */

    private readonly AudioFormat _fmt;
    private readonly Fft _fft;
    private readonly int _bins;
    private readonly double _dt;

    private readonly float[] _hann;
    private readonly float[] _monoRing, _leftRing, _rightRing;
    private readonly float[] _windowScratch;
    private readonly float[] _mag, _prevMag;
    private readonly int _ringMask;
    private long _written;                        // frames appended
    private long _consumed;                       // frames the analyzer has stepped past
    private readonly float[] _partial;            // carries a frame split across calls
    private int _partialCount;

    private readonly int[] _bandBinLo = new int[8];
    private readonly int[] _bandBinHi = new int[8];
    private readonly double[] _bandEnv = new double[8];
    private readonly double[] _bandHi = new double[8];
    private readonly double[] _bandLo = new double[8];
    private readonly double[] _bandNorm = new double[8];
    private readonly double[] _bandEma8s = new double[8];

    private readonly Normalizer _rmsNorm = new(), _peakNorm = new();

    // Crest factor and spectral centroid are not levels: a peak-following envelope makes no
    // sense for them and pinned both near 1. Symmetric envelope, and no level calibration.
    private readonly Normalizer _crestNorm = new(peakFollow: false);
    private readonly Normalizer _centroidNorm = new(peakFollow: false);
    private double _loudSlow;

    private readonly float[] _fluxHist = new float[FluxHistory];
    private readonly float[] _fluxSort = new float[FluxHistory];
    private int _fluxAt;
    private double _prevFlux;

    /// <summary>Raw (unnormalized) spectral rise, this hop and the previous one. See Flux().</summary>
    private double _rise, _prevRise;

    /// <summary>Spectral rise in the kick band only, used as the BEAT strength. See Flux().</summary>
    private double _riseLow, _prevRiseLow;

    /// <summary>
    /// F_FLUX's own normalizer, independent of the onset detector. Symmetric envelope
    /// because flux is a rate of change, not a level: a peak follower on it would track
    /// its maximum and sit there. See the note in <see cref="Onset"/>.
    /// </summary>
    private readonly Normalizer _fluxNorm = new(peakFollow: false, spanFloor: 0.015,
                                                envTau: FluxEnvTau, windowOnEnvelope: true);
    private double _fluxEnvN;
    private double _onsetImpulse;
    private double _lastOnsetTime = -1;

    private readonly float[] _onsetHist;

    /// <summary>
    /// The same onset envelope, but with each impulse scaled by the size of the flux peak
    /// that fired it. Used only by <see cref="BeatCombRatio"/>. See the note there for why
    /// the unit-height envelope cannot answer the beat-versus-subdivision question.
    /// </summary>
    private readonly float[] _strengthHist;
    private double _onsetStrength;

    /// <summary>Post-onset detection bar, decaying. See <see cref="OnsetGateTau"/>.</summary>
    private double _onsetGate;
    private int _onsetHistAt;
    private readonly float[] _bpmHist;
    private readonly float[] _bpmSort;
    private int _bpmHistAt, _bpmHistCount;

    private double _bpm, _bpmConf, _beatPhase, _beatCount;
    private bool _beatFromOnset;

    private readonly int[] _chromaPc;             // bin -> pitch class, or -1 (diagnostics)
    private readonly double[] _chroma = new double[12];
    private readonly double[] _chromaEma = new double[12];

    private int _keyPc;
    private bool _keyMinor;
    private double _keyConf;

    // HPSS-lite history: 5 frames of the magnitude spectrogram, band-limited to keep the
    // median filters cheap. Percussivity above ~10 kHz adds nothing a listener notices.
    /// <summary>
    /// §7.5 specifies a time window of 5 and a frequency window of 17. At a 512-sample hop
    /// 5 frames is 53 ms, which is about as long as the drum hit it is supposed to reject:
    /// a click filled the whole window, the temporal median stayed high, and a click track
    /// measured LESS percussive than a sustained pad. 17 frames is 183 ms — long enough
    /// that a transient is a minority of the window, which is the property the median is
    /// there to exploit.
    /// </summary>
    private const int HpssFrames = 17, HpssFreqWindow = 17;
    private readonly float[][] _hpss;
    private int _hpssAt, _hpssFilled;
    private double _percussivity = 0.5;
    private readonly int _hpssBins;
    private readonly float[] _hpssScratch = new float[Math.Max(HpssFrames, HpssFreqWindow)];

    private double _rmsMid, _rmsSide, _rmsLeft, _rmsRight;

    private FeatureFrame _last;
    private double _time;

    private static readonly string[] StageNames =
        ["bands", "levels", "flux", "onset", "tempo", "chroma", "stereo", "percussivity",
         "novelty", "fft"];
    private readonly long[] _stageTicks = new long[StageNames.Length];

    private long Mark(int stage, long since)
    {
        long now = Stopwatch.GetTimestamp();
        _stageTicks[stage] += now - since;
        return now;
    }

    /// <summary>Per-stage cost in ms per hop, for the §11 budget breakdown.</summary>
    public IEnumerable<(string Name, double MsPerHop)> StageCostMs()
    {
        for (int i = 0; i < StageNames.Length; i++)
            yield return (StageNames[i], StepCount > 0
                ? Stopwatch.GetElapsedTime(0, _stageTicks[i]).TotalMilliseconds / StepCount
                : 0);
    }

    public Analyzer(AudioFormat format)
    {
        if (format.SampleRate <= 0) throw new ArgumentException("sample rate must be positive");
        if (format.Channels is < 1 or > 8) throw new ArgumentException("channels must be 1..8");

        _fmt = format;
        _dt = format.HopSeconds;
        _fft = new Fft(AudioFormat.Window);
        _bins = Fft.Bins(AudioFormat.Window);

        // Ring long enough that a late analysis thread does not lose the window it is
        // about to read. Four windows is ~170 ms at 48 kHz.
        int ring = AudioFormat.Window * 4;
        _monoRing = new float[ring];
        _leftRing = new float[ring];
        _rightRing = new float[ring];
        _ringMask = ring - 1;

        _partial = new float[format.Channels];
        _windowScratch = new float[AudioFormat.Window];
        _mag = new float[_bins];
        _prevMag = new float[_bins];

        _hann = new float[AudioFormat.Window];
        for (int i = 0; i < AudioFormat.Window; i++)
            _hann[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (AudioFormat.Window - 1)));

        // Band edges in bins, from the ACTUAL rate. Plan §2.3: never assume 48000.
        //
        // Round rather than floor/ceil, and stop one bin short of the next band's start:
        // floor for the low edge and ceil for the high edge made adjacent bands SHARE
        // bins, so a 38 Hz tone lit sub and bass equally because bins 2-3 belonged to
        // both.
        for (int b = 0; b < 8; b++)
        {
            int lo = Math.Max(1, (int)Math.Round(BandEdges[b].Lo / _fmt.BinHz));
            int hi = (int)Math.Round(BandEdges[b].Hi / _fmt.BinHz) - 1;
            _bandBinLo[b] = Math.Min(lo, _bins - 1);
            _bandBinHi[b] = Math.Clamp(hi, _bandBinLo[b], _bins - 1);
        }

        _onsetHist = new float[Math.Max(16, (int)(TempoHistorySeconds * _fmt.FrameRate))];
        _strengthHist = new float[_onsetHist.Length];
        // Sized by how often the estimate is PRODUCED, not by the hop rate: the
        // autocorrelation only runs every TempoEvery hops, so counting slots in hops made
        // the 2 s median span 8 s of wall time and put the drift error back over budget
        // (2.90 BPM against §14's 2.0).
        int bpmSlots = Math.Max(8, (int)(BpmMedianSeconds * _fmt.FrameRate / TempoEvery));
        _bpmHist = new float[bpmSlots];
        _bpmSort = new float[bpmSlots];

        // bin -> pitch class, precomputed. round(12*log2(f/440)) is semitones from A440;
        // +9 rotates it so 0 is C, matching features.glsl's "C at [0].x".
        //
        // The weight is §7.4's log-frequency taper: bins per semitone grows in proportion
        // to frequency, so a flat sum of magnitude weights the top octave twelve times the
        // bottom one and the harmonics outvote the fundamentals. 1/f converts a
        // magnitude-per-bin sum into magnitude-per-semitone, which is what a chroma vector
        // is supposed to be.
        //
        // It is not cosmetic. Without it a C major triad rendered with two harmonics per
        // note detected as E MINOR, because C's third harmonic is a G and E's is a B: the
        // harmonics of a C chord spell out its relative-ish minor more strongly than the
        // fundamentals spell out C.
        _chromaPc = new int[_bins];
        for (int k = 0; k < _bins; k++)
        {
            double f = k * _fmt.BinHz;
            if (f < ChromaLoHz || f > ChromaHiHz) { _chromaPc[k] = -1; continue; }
            int semis = (int)Math.Round(12 * Math.Log2(f / 440.0));
            _chromaPc[k] = ((semis + 9) % 12 + 12) % 12;
        }

        _hpssBins = Math.Min(_bins, Math.Max(32, (int)(10000 / _fmt.BinHz)));
        _hpss = new float[HpssFrames][];
        for (int i = 0; i < HpssFrames; i++) _hpss[i] = new float[_hpssBins];

        Reset();
    }

    public AudioFormat Format => _fmt;

    /* ----------------------------------------------------------- diagnostics */

    public double Bpm => _bpm;
    public double BpmConfidence => _bpmConf;

    /// <summary>True while the §7.3 low-confidence fallback is driving beat phase.</summary>
    public bool BeatFromOnset => _beatFromOnset;

    public int KeyPitchClass => _keyPc;
    public bool KeyIsMinor => _keyMinor;
    public double KeyConfidence => _keyConf;
    public long StepCount { get; private set; }

    /// <summary>
    /// Linear RMS of the last window, BEFORE normalization.
    ///
    /// §11's idle rule is "RMS below −50 dBFS for 3 s", which is an absolute statement, and
    /// no field in the frame can answer it: every one of them is normalized against its own
    /// recent range, which is exactly what makes quiet passages look alive. So the absolute
    /// level has to come out separately.
    /// </summary>
    public double RawRms { get; private set; }

    /// <summary>
    /// Per-band internals in dB, for diagnosing a band that reads wrong. Internal rather
    /// than public: this is the shape of the normalizer, not part of the feature contract.
    /// </summary>
    internal double BandEnvDb(int b) => _bandEnv[b];
    internal double BandHiDb(int b) => _bandHi[b];
    internal double BandLoDb(int b) => _bandLo[b];
    /// <summary>Autocorrelation shape, for diagnosing a confidence that will not rise.</summary>
    /// <summary>The chroma vector, C at index 0. For diagnosing a key that reads wrong.</summary>
    internal ReadOnlySpan<double> ChromaVector => _chroma;

    internal double HpssP { get; private set; }
    internal double HpssH { get; private set; }
    internal double AcfPeak { get; private set; }
    internal double AcfMean { get; private set; }
    internal double AcfStd { get; private set; }
    internal double Acf0 { get; private set; }
    internal int AcfLag { get; private set; }

    internal double BandGateRefDb
    {
        get
        {
            double r = double.NegativeInfinity;
            for (int b = 0; b < 8; b++) r = Math.Max(r, _bandHi[b]);
            return r;
        }
    }
    public FeatureFrame LastFrame => _last;

    /// <summary>Analyzed audio time in seconds — hops * hopSeconds, not wall clock.</summary>
    public double AnalyzedSeconds => _time;

    public void Reset()
    {
        Array.Clear(_monoRing); Array.Clear(_leftRing); Array.Clear(_rightRing);
        Array.Clear(_mag); Array.Clear(_prevMag);
        _written = _consumed = 0;
        Array.Clear(_partial); _partialCount = 0;

        for (int b = 0; b < 8; b++)
        {
            // Start the envelopes at the noise floor so the first frames read as silence
            // rather than as a step from zero, which would fire a false onset.
            _bandEnv[b] = _bandHi[b] = _bandLo[b] = -120;
            _bandNorm[b] = 0;
            _bandEma8s[b] = 0;
        }
        _rmsNorm.Reset(); _peakNorm.Reset(); _crestNorm.Reset(); _centroidNorm.Reset();
        _loudSlow = 0;

        Array.Clear(_fluxHist); _fluxAt = 0;
        _prevFlux = 0; _rise = 0; _prevRise = 0;
        _riseLow = 0; _prevRiseLow = 0;
        _fluxNorm.Reset(); _fluxEnvN = 0;
        _onsetImpulse = 0; _lastOnsetTime = -1; _onsetGate = 0;

        Array.Clear(_onsetHist); Array.Clear(_strengthHist);
        _onsetStrength = 0; _onsetHistAt = 0;
        CombRatio = 1; _combSmooth = 1; _octaveDoubled = false;
        OnsetCount = 0;
        Array.Clear(_bpmHist); _bpmHistAt = 0; _bpmHistCount = 0;
        _bpm = 0; _bpmConf = 0; _beatPhase = 0; _beatCount = 0; _beatFromOnset = false;

        Array.Clear(_chroma); Array.Clear(_chromaEma);
        _keyPc = 0; _keyMinor = false; _keyConf = 0;

        foreach (var f in _hpss) Array.Clear(f);
        _hpssAt = _hpssFilled = 0;
        _percussivity = 0.5;

        StepCount = 0;
        Array.Clear(_stageTicks);
        _time = 0;
        RawRms = 0;
        _last = default;
    }

    /* ---------------------------------------------------------------- input */

    /// <summary>
    /// Append interleaved samples in device channel order. Any length: production feeds
    /// arrive in whatever buffer WASAPI hands over, never in neat hop multiples.
    ///
    /// A call that ends mid-frame carries the remainder over to the next one. That is not
    /// theoretical tidiness — an earlier version divided by the channel count and dropped
    /// the partial frame, which shifted the channel parity of everything after it and
    /// deleted one sample per call. At a 1777-float chunk size that is a click every
    /// 37 ms, and it put a broadband floor 40 dB below a pure tone into every band. The
    /// band-isolation test read 0.98 on all eight bands and looked like a normalizer bug.
    /// </summary>
    public void Append(ReadOnlySpan<float> interleaved)
    {
        int ch = _fmt.Channels;

        // Finish the frame left over from last time before anything else.
        while (_partialCount > 0 && interleaved.Length > 0)
        {
            int take = Math.Min(ch - _partialCount, interleaved.Length);
            interleaved[..take].CopyTo(_partial.AsSpan(_partialCount));
            _partialCount += take;
            interleaved = interleaved[take..];
            if (_partialCount < ch) return;       // still short; wait for more
            PushFrame(_partial);
            _partialCount = 0;
        }

        int frames = interleaved.Length / ch;
        for (int i = 0; i < frames; i++)
            PushFrame(interleaved.Slice(i * ch, ch));

        int rem = interleaved.Length - frames * ch;
        if (rem > 0)
        {
            interleaved[^rem..].CopyTo(_partial);
            _partialCount = rem;
        }
    }

    private void PushFrame(ReadOnlySpan<float> frame)
    {
        float l = frame[0];
        float r = frame.Length > 1 ? frame[1] : l;
        int at = (int)(_written & _ringMask);
        _leftRing[at] = l;
        _rightRing[at] = r;
        _monoRing[at] = 0.5f * (l + r);           // mono sum for all spectral work
        _written++;
    }

    /// <summary>
    /// Analyze one hop if a full window is available. Returns false when there is not yet
    /// enough audio, which is the normal case between callbacks.
    ///
    /// Only the DSP-owned fields are written. Time and resolution belong to the render
    /// host — see FF.HostOwned.
    /// </summary>
    public bool TryStep(ref FeatureFrame frame)
    {
        if (_written - _consumed < AudioFormat.Hop) return false;
        if (_written < AudioFormat.Window) return false;

        _consumed += AudioFormat.Hop;
        _time = _consumed / (double)_fmt.SampleRate;

        // The most recent Window samples, Hann-windowed.
        long start = _written - AudioFormat.Window;
        for (int i = 0; i < AudioFormat.Window; i++)
            _windowScratch[i] = _monoRing[(int)((start + i) & _ringMask)] * _hann[i];

        long tFft = Stopwatch.GetTimestamp();
        _mag.AsSpan().CopyTo(_prevMag);
        _fft.Magnitude(_windowScratch, _mag);
        Mark(9, tFft);

        var f = frame.AsSpan();

        // Stage timing is always on: it is a handful of QueryPerformanceCounter calls
        // against a millisecond of work, and §11's budget is a gate rather than a hope.
        long t = Stopwatch.GetTimestamp();
        Bands(f);                       t = Mark(0, t);
        Levels(f, start);               t = Mark(1, t);
        double flux = Flux();           t = Mark(2, t);
        Onset(f, flux);                 t = Mark(3, t);
        Tempo(f);                       t = Mark(4, t);
        Chroma(f);                      t = Mark(5, t);
        Stereo(f);                      t = Mark(6, t);
        Percussivity(f);                t = Mark(7, t);
        Novelty(f);                     _ = Mark(8, t);

        // Phase 3 owns these; a Phase 2 frame states them explicitly rather than leaving
        // whatever the caller's struct happened to contain.
        f[FF.Intensity] = 1;
        f[FF.Era] = 0;
        f[FF.SongMode] = 0;
        f[FF.PaletteMix] = 0;
        f[FF.TrackPos] = (float)_time;
        f[FF.TrackFrac] = 0;
        f[FF.SectionPhase] = (float)(_time / 60.0 % 1.0);
        f[52] = f[53] = f[54] = f[55] = 0;

        StepCount++;
        _last = frame;
        return true;
    }

    /* ------------------------------------------------------------ §7.1 bands */

    /// <summary>
    /// How far below the loudest band a band has to sit before it is treated as absent.
    ///
    /// Without this the per-band normalizer amplifies its own noise floor: a band carrying
    /// nothing but FFT leakage at -100 dBFS still has a hi/lo window, and (env - lo) / span
    /// drives it to 1.0. A pure 1550 Hz tone lit every one of the eight bands to ~0.93.
    ///
    /// A gate is the right mechanism rather than a tuning fix, because the per-band
    /// normalizer reports ~1.0 for ANY band whose content is stationary — that is what
    /// "normalize each band against its own recent range" means. Nothing inside the band
    /// can distinguish "steadily loud" from "steadily almost absent"; only a comparison
    /// across bands can.
    ///
    /// The gate is RELATIVE to the loudest band, never absolute, so it survives a level
    /// change — which is the whole point of §7.1's adaptive normalization and what
    /// NormalizationTests.Adaptive_normalization_is_level_independent pins down.
    ///
    /// Thresholds: an 8-band split of real program material keeps every band that matters
    /// within ~35 dB of the loudest, and 55 dB down is inaudible next to it. Hann leakage
    /// from a pure tone measures 105-130 dB down, so content and leakage are never close
    /// to the boundary.
    /// </summary>
    private const double GateFullDb = -35, GateZeroDb = -55;

    /// <summary>
    /// How fast the normalization window's edges move TOWARD a new extreme.
    ///
    /// §7.1 writes the window as `hi = db > hi ? db : ...`, i.e. the edges jump to a new
    /// extreme instantly and relax back slowly. That makes them a peak-and-trough tracker,
    /// and the span it reports is the full excursion of the last few seconds — including
    /// the deepest momentary gap between two notes. An envelope with a 250 ms release sits
    /// near the top of that span almost all the time, so every normalized field came out
    /// top-heavy: measured medians of 0.80-0.92 against the synthetic driver's 0.21-0.55.
    /// Presets authored against the latter would be driven flat out on real audio.
    ///
    /// The cause is structural, not a tuning error. §7.1's envelope has a 10 ms attack,
    /// which makes it a PEAK FOLLOWER: it catches every transient and holds for 250 ms, so
    /// over dense material it sits within a couple of dB of the 3 s maximum almost
    /// continuously. (Two alternatives were tried and measured. Tracking the window off the
    /// envelope rather than the raw dB left the bands at 0.79-0.85. Giving the window edges
    /// a short time constant toward a new extreme was worse still, pinning every field at
    /// 1.000, because the edge then follows the envelope even more closely.)
    ///
    /// So the window stays exactly as §7.1 specifies and the distribution is corrected
    /// explicitly, with one monotonic curve on the output. Gamma is FITTED, not guessed:
    /// v^4.66 maps the measured real median of 0.83 onto the synthetic driver's 0.42, which
    /// is what makes a preset authored in the harness behave the same on real audio.
    ///
    /// Being monotonic, it changes no ordering and no isolation property: 0 stays 0, 1
    /// stays 1, and a band that is loud relative to its own history still reads high.
    ///
    /// It applies to LEVEL-type fields only — the eight bands, rms, peak, loudSlow. Spectral
    /// centroid and crest factor are not levels; they get a symmetric envelope instead (see
    /// Normalizer), which centres them without any curve.
    /// </summary>
    private const double LevelGamma = 4.66;

    /// <summary>
    /// Per-band calibration exponents, replacing the single <see cref="LevelGamma"/> for the
    /// eight bands. FITTED, not guessed — `gamma[b] = ln(synthetic_median[b]) / ln(pre[b])`,
    /// where `pre[b]` is the normalizer's own output recovered by inverting the committed
    /// scalar curve on the real capture.
    ///
    /// WHY ONE EXPONENT COULD NOT DO IT. The real-versus-synthetic median deltas run +0.04 at
    /// `sub` to +0.41 at `air`. A single exponent lands the middle bands and leaves `sub`
    /// under-corrected and `air` over-corrected, and the whole point of the calibration is
    /// that a preset authored in the harness behaves the same on real audio — which is a
    /// per-band property, not an aggregate one.
    ///
    /// READ THE SIZE OF THESE NUMBERS AS A SYMPTOM. Needing 9.75 to 45.59 means the
    /// normalizer's pre-calibration output is pinned hard against 1.0 — measured 0.886 to
    /// 0.980 at the median, with `lowMid` spanning only 0.928..0.997 from p10 to p90. The
    /// curve is expanding a 7%-of-full-scale input range across most of the output, so it is
    /// doing real work, but it is compensating for §7.1's peak-follower envelope rather than
    /// fixing it. See the LevelGamma note for why the envelope itself is left alone.
    ///
    /// THE COEFFICIENTS ARE NOT FITTED YET, AND THAT IS A MEASUREMENT, NOT AN OMISSION. All
    /// eight are the old scalar, so this change is behaviour-neutral until there is data to
    /// fit against. Running the fit on the two materials available gave:
    ///
    ///     band         from the capture    from MusicLike    disagreement
    ///     sub                     9.75              5.23           1.9x
    ///     bass                   18.52              5.20           3.6x
    ///     lowMid                 45.59              9.89           4.6x
    ///     mid                    26.14             12.24           2.1x
    ///     hiMid                  22.86              6.30           3.6x
    ///     presence               14.81              9.28           1.6x
    ///     brilliance             18.59             11.55           1.6x
    ///     air                    22.86             14.92           1.5x
    ///
    /// The same procedure on two pieces of material disagrees by up to 4.6x, because the
    /// exponent MULTIPLIES the material difference: at gamma 45, a pre-gamma median of 0.95
    /// instead of 0.98 moves the output from 0.40 to 0.10. Committing the capture-fitted
    /// column was tried and measured — it pinned `lowMid` dead at 0.014 on MusicLike and made
    /// the aggregate worse, 0.192 to 0.205 mean absolute delta. A calibration that depends
    /// this strongly on which recording it was fitted to is not a calibration.
    ///
    /// What both columns DO agree on: every band needs more correction than 4.66. So the
    /// direction is real and the magnitude is not knowable from one recording. Fit against the
    /// five real captures with `tools/fit-band-gamma.mjs` and replace this array.
    ///
    /// The deeper reading is that gamma is the wrong layer. Needing exponents this large means
    /// §7.1's peak-follower envelope has already thrown the range away upstream — pre-gamma
    /// `lowMid` spans 0.928..0.997 from p10 to p90 on real audio, 7% of full scale — and a
    /// curve that expands 7% into most of 0..1 is amplifying whatever noise is in those 7%.
    /// </summary>
    private static readonly double[] BandGamma =
        [LevelGamma, LevelGamma, LevelGamma, LevelGamma,
         LevelGamma, LevelGamma, LevelGamma, LevelGamma];

    /// <summary>
    /// rms and peak get their own, and loudSlow deliberately does NOT: it is a 3 s EMA of the
    /// already-calibrated rms, so giving it a third exponent would apply the correction twice.
    /// </summary>
    private const double RmsGamma = LevelGamma, PeakGamma = LevelGamma;

    private static double Calibrate(double v, double gamma) => Math.Pow(v, gamma);

    private void Bands(Span<float> f)
    {
        double attack = 1 - Math.Exp(-_dt / 0.010);
        double release = 1 - Math.Exp(-_dt / 0.250);
        double hiRate = 1 - Math.Exp(-_dt / 3.0);
        double loRate = 1 - Math.Exp(-_dt / 8.0);


        for (int b = 0; b < 8; b++)
        {
            int lo = _bandBinLo[b], hi = _bandBinHi[b];
            double e = 0;
            for (int k = lo; k <= hi; k++) e += (double)_mag[k] * _mag[k];
            e /= hi - lo + 1;

            // The window's coherent gain would otherwise make absolute dB depend on the
            // window length; only the relative range matters here, so leave it in and let
            // the adaptive normalizer absorb it.
            double db = 10 * Math.Log10(e + 1e-12);

            _bandEnv[b] += (db - _bandEnv[b]) * (db > _bandEnv[b] ? attack : release);

            // §7.1 verbatim. Fast to open up, slow to close down: a Dark Star trough must
            // not drag the floor up behind it and flatten the next loud passage.
            _bandHi[b] = db > _bandHi[b] ? db : _bandHi[b] + (db - _bandHi[b]) * hiRate;
            _bandLo[b] = db < _bandLo[b] ? db : _bandLo[b] + (db - _bandLo[b]) * loRate;

            // The 6 dB floor is what stops silence being amplified into noise.
            double span = Math.Max(_bandHi[b] - _bandLo[b], 6.0);
            double v = (_bandEnv[b] - _bandLo[b]) / span;
            _bandNorm[b] = Calibrate(Math.Clamp(v, 0, 1), BandGamma[b]);
        }

        // Reference level for the gate: the loudest band's recent peak, so a momentary dip
        // in the mix cannot un-mute a band that carries nothing.
        double refDb = double.NegativeInfinity;
        for (int b = 0; b < 8; b++) refDb = Math.Max(refDb, _bandHi[b]);

        for (int b = 0; b < 8; b++)
        {
            double relative = _bandEnv[b] - refDb;
            double gate = Math.Clamp((relative - GateZeroDb) / (GateFullDb - GateZeroDb), 0, 1);
            _bandNorm[b] *= gate;
            f[FF.Sub + b] = (float)_bandNorm[b];
        }
    }

    /* ------------------------------------------------- levels, centroid, etc */

    private void Levels(Span<float> f, long windowStart)
    {
        // One pass over the window for mono level, peak and mid/side. Three separate loops
        // over 2048 samples read the same ring three times for no reason.
        double sum = 0, peak = 0, sm = 0, ss = 0, sl = 0, sr = 0;
        for (int i = 0; i < AudioFormat.Window; i++)
        {
            int at = (int)((windowStart + i) & _ringMask);
            float s = _monoRing[at];
            sum += (double)s * s;
            double abs = Math.Abs(s);
            if (abs > peak) peak = abs;

            double l = _leftRing[at], r = _rightRing[at];
            double m = 0.5 * (l + r), sd = 0.5 * (l - r);
            sm += m * m; ss += sd * sd; sl += l * l; sr += r * r;
        }
        _rmsMid = Math.Sqrt(sm / AudioFormat.Window);
        _rmsSide = Math.Sqrt(ss / AudioFormat.Window);
        _rmsLeft = Math.Sqrt(sl / AudioFormat.Window);
        _rmsRight = Math.Sqrt(sr / AudioFormat.Window);

        double rms = Math.Sqrt(sum / AudioFormat.Window);
        RawRms = rms;

        double rmsDb = 20 * Math.Log10(rms + 1e-9);
        double peakDb = 20 * Math.Log10(peak + 1e-9);

        double rmsN = Calibrate(_rmsNorm.Step(rmsDb, _dt), RmsGamma);
        double peakN = Calibrate(_peakNorm.Step(peakDb, _dt), PeakGamma);

        f[FF.Rms] = (float)rmsN;
        f[FF.Peak] = (float)peakN;

        // 3 s EMA, per the field comment in features.glsl.
        _loudSlow += (rmsN - _loudSlow) * (1 - Math.Exp(-_dt / 3.0));
        f[FF.LoudSlow] = (float)_loudSlow;

        // Crest factor in dB, normalized on its own window — NOT the difference of the two
        // already-normalized fields. rms and peak are each normalized against their own
        // recent range, so on steady material both sit near the top of it and their
        // difference is ~0 regardless of how peaky the audio actually is. Measured 0.000
        // against the synthetic driver's 0.28, which would leave every dynamics-driven
        // parameter dead on real audio.
        f[FF.Dynamics] = (float)_crestNorm.Step(peakDb - rmsDb, _dt);   // symmetric, uncalibrated

        // Spectral centroid, mapped logarithmically between 50 Hz and 8 kHz — linear Hz
        // would put every musical signal in the bottom tenth of the range.
        double num = 0, den = 0;
        for (int k = 1; k < _bins; k++)
        {
            double m = _mag[k];
            num += m * (k * _fmt.BinHz);
            den += m;
        }
        double centroidHz = den > 1e-9 ? num / den : 0;

        // Normalize the centroid against its own recent range rather than against a fixed
        // 50 Hz - 8 kHz map. A fixed map reports "how bright is this, absolutely", which
        // pins at 1.000 on cymbal-heavy material and near 0 on anything dark; the adaptive
        // form reports "how bright is this passage FOR THIS TRACK", which is what a visual
        // parameter wants and what every other field in the frame already means. Fed in
        // octaves so the normalizer's dB-shaped window applies sensibly.
        double centroidOct = centroidHz > 20 ? 6 * Math.Log2(centroidHz) : 0;
        f[FF.Centroid] = (float)_centroidNorm.Step(centroidOct, _dt);
    }

    /* ------------------------------------------------------- §7.2 flux/onset */

    private double Flux()
    {
        int lo = Math.Max(1, (int)(FluxLoHz / _fmt.BinHz));
        int hi = Math.Min(_bins - 1, (int)(FluxHiHz / _fmt.BinHz));

        // The kick-band rise, kept for diagnostics only. It was TRIED as the beat-strength
        // weight for BeatCombRatio, on the reasoning that the beat is where the kick is, and
        // it was measurably worse: a click track's clicks are lowpassed noise bursts whose
        // low-band content varies randomly from click to click, so the comb read that variance
        // as alternation and HALVED tempi that had been correct for the whole project —
        // 120 BPM to 60.00, 150 BPM to 74.01. The flat rise leaves click tracks at comb
        // 1.05-1.12, well under the threshold. Recorded rather than deleted so the next person
        // does not re-derive it.
        int lowHi = Math.Min(_bins - 1, (int)(150.0 / _fmt.BinHz));

        double rise = 0, total = 0, riseLow = 0;
        for (int k = lo; k <= hi; k++)
        {
            double d = _mag[k] - _prevMag[k];
            if (d > 0)
            {
                rise += d;
                if (k <= lowHi) riseLow += d;
            }
            total += _mag[k];
        }
        _riseLow = riseLow;

        // The RATIO is what §7.2 wants for detection and for F_FLUX: it is level-invariant, so
        // the threshold means the same thing in a loud passage and a quiet one.
        //
        // But level invariance is exactly wrong for measuring how BIG an onset was, and
        // BeatCombRatio needs that. Measured: with the ratio as the strength weight, the
        // on-beat and off-beat combs of MusicLike scored 1.01 — indistinguishable — because a
        // lone hat divides a small rise by a small total and lands as high as a kick, bass and
        // chord hitting together. So the raw rise is kept alongside. It is the standard onset
        // strength measure and it is level-sensitive, which is the property being asked for.
        _rise = rise;
        return rise / (total + 1e-9);
    }

    private void Onset(Span<float> f, double flux)
    {
        _fluxHist[_fluxAt] = (float)flux;
        _fluxAt = (_fluxAt + 1) % FluxHistory;

        _fluxHist.AsSpan().CopyTo(_fluxSort);
        Array.Sort(_fluxSort);
        double median = _fluxSort[FluxHistory / 2];

        double thr = median * 1.4 + 0.02;
        bool refractoryOver = _lastOnsetTime < 0 || _time - _lastOnsetTime >= OnsetRefractory;

        // The post-onset gate decays back toward the ordinary threshold; the bar is whichever
        // is higher. See OnsetGateTau for the measurement this exists to fix.
        _onsetGate *= Math.Exp(-_dt / OnsetGateTau);
        double bar = Math.Max(thr, _onsetGate);

        // Crossing the threshold is not enough — the flux must also be FALLING, i.e. the
        // previous frame was the local peak. Without it, dense material sits above the
        // adaptive threshold for many frames in a row and re-triggers every time the 50 ms
        // refractory expires: measured on real audio, the onset impulse never decayed below
        // 0.53 and averaged 0.84, so every onset-driven motif fired continuously.
        bool peaked = _prevFlux >= flux && _prevFlux > bar;
        double peakFlux = _prevFlux;
        // The rise that belongs to the PEAK hop, not to this one — detection is one hop
        // behind, and pairing a peak with the following hop's rise would weight every onset
        // by its own decay rather than its attack.
        double peakRise = _prevRise;
        _prevFlux = flux;
        _prevRise = _rise;
        _prevRiseLow = _riseLow;

        if (peaked && refractoryOver)
        {
            _onsetImpulse = 1;
            OnsetCount++;
            // Arm the gate against this peak, so the shoulders of THIS event cannot each
            // count as their own onset.
            _onsetGate = peakFlux * OnsetGateMultiplier;
            // The SAME impulse, weighted by how big the onset actually was. F_ONSET stays a
            // unit impulse — presets gate motifs on `onset > 0.82` — but that unit height
            // throws away the one thing that separates a beat from a subdivision, so the
            // strength is kept alongside rather than folded in.
            //
            // Weighted by the raw RISE, not by the flux ratio: see the note in Flux(). Left
            // unnormalized, because BeatCombRatio only ever compares two combs of this signal
            // against each other and a common scale factor cancels in the ratio.
            _onsetStrength = peakRise;
            _lastOnsetTime = _time;
        }
        else
        {
            double k = Math.Exp(-_dt / OnsetDecay);
            _onsetImpulse *= k;
            _onsetStrength *= k;
        }

        // F_FLUX is §7.2's INDEPENDENT continuous drive, not a view of the onset detector.
        // It used to be `clamp(detectFlux / (thr * 1.6))`, which was wrong twice over and
        // measured p50 0.000 / p90 1.000 — two spikes and almost nothing between:
        //
        //  1. The divisor was the onset comparator's own threshold, so the field saturated
        //     at 1 on essentially every onset and carried the same information as F_ONSET.
        //     A clamp against a THRESHOLD is a decision, not a normalization.
        //  2. Raw flux is a half-wave-rectified difference: `rise` sums only the positive
        //     bin deltas, so on the hops where the spectrum is falling it is ~0. Measured
        //     42% of frames below 0.02 on music-like material and 91% on a click track.
        //     That is the zero spike, and no choice of divisor removes it.
        //
        // So smooth first — a 55 ms symmetric envelope bridges the rectification zeros
        // without blurring gesture-scale change — then map onto the observed range with
        // the clamp applied AFTER the affine map. The span floor is in the ratio's own
        // units; 6.0 (the dB default) would be six times the whole range.
        _fluxEnvN = _fluxNorm.Step(flux, _dt);
        f[FF.Flux] = (float)_fluxEnvN;
        f[FF.Onset] = (float)_onsetImpulse;

        _onsetHist[_onsetHistAt] = (float)_onsetImpulse;
        _strengthHist[_onsetHistAt] = (float)_onsetStrength;
        _onsetHistAt = (_onsetHistAt + 1) % _onsetHist.Length;
    }

    /* ------------------------------------------------------------ §7.3 tempo */

    /// <summary>
    /// Is <paramref name="lag"/> the beat, or half of it? Returns the strong comb's mean
    /// over the weak comb's, folded at <c>2 * lag</c> — 1.0 means no alternation at all.
    ///
    /// THE ACF CANNOT ANSWER THIS, and that is not a tuning problem. When dense material
    /// carries a steady subdivision — hats on the half beat, a ride on eighths — the onset
    /// envelope's strongest periodicity genuinely IS the subdivision, so acf[lag] is the
    /// peak and acf[2*lag] is necessarily no larger. Every score that is monotone in the
    /// ACF therefore has the subdivision winning by construction, harmonic sums included:
    /// measured, MusicLike at 88 BPM reported 170.18 with confidence 0.88 both before and
    /// after a harmonic-sum rewrite, because promoting the longer lag would have required
    /// acf[66] &gt; 2.35 * acf[33] and acf[33] was the peak.
    ///
    /// What does separate them is that beats are LOUDER than the subdivisions between them
    /// — kick and bass on the beat, hats alone off it. So fold the strength-weighted onset
    /// envelope at 2*lag, find the strongest phase, and compare it against the phase half a
    /// period away. A metronomic click track alternates not at all and scores ~1, so this
    /// cannot disturb a source that was already right.
    /// </summary>
    private double BeatCombRatio(int lag)
    {
        int n = _strengthHist.Length;
        int period = lag * 2;
        // Two full periods minimum, or the combs are averages of one or two taps each and
        // the ratio is noise.
        if (lag < 1 || period * 2 > n) return 1;

        double bestMean = -1;
        int bestPhase = 0;
        Span<double> comb = stackalloc double[period];

        for (int phase = 0; phase < period; phase++)
        {
            double sum = 0;
            int taps = 0;
            for (int i = phase; i < n; i += period)
            {
                // Newest first, same walk as the autocorrelation, so the two agree about
                // which end of the circular buffer is "now".
                sum += _strengthHist[(_onsetHistAt - 1 - i + n) % n];
                taps++;
            }
            comb[phase] = taps > 0 ? sum / taps : 0;
            if (comb[phase] > bestMean) { bestMean = comb[phase]; bestPhase = phase; }
        }

        double weak = comb[(bestPhase + lag) % period];
        if (bestMean <= 1e-12) return 1;
        double ratio = weak > 1e-12 ? bestMean / weak : double.PositiveInfinity;

        // An off-beat comb that is EMPTY is not evidence of alternation. It is evidence that
        // there are no off-beat onsets at all — and material with no subdivision has no
        // subdivision to mistake for the beat, so the raw peak was already right. The honest
        // answer above the limit is therefore "no alternation", not a very large number.
        //
        // This is what the first cut got wrong. The ratio was unbounded, and on arrhythmic
        // clicks it reached 3.8e6 and dragged the smoothed value past every threshold for the
        // rest of the run — halving the reported tempo of exactly the free-time population
        // §7.3 exists to leave alone.
        return ratio > CombSparseLimit ? 1 : ratio;
    }

    /// <summary>
    /// How many onsets have actually been detected. Divided by elapsed time this is the
    /// onset RATE, which is what separates "this material is dense" from "the detector is
    /// retriggering" — two explanations for a saturated envelope that look identical in the
    /// envelope itself.
    /// </summary>
    public long OnsetCount { get; private set; }

    /// <summary>Last comb ratio computed, for the octave diagnostics.</summary>
    public double CombRatio { get; private set; } = 1;

    /// <summary>The smoothed ratio and the latched decision, for the octave diagnostics.</summary>
    public double CombSmooth => _combSmooth;
    public bool OctaveDoubled => _octaveDoubled;

    private double _combSmooth = 1;
    private bool _octaveDoubled;

    private void Tempo(Span<float> f)
    {
        // The phase accumulator runs every hop; only the autocorrelation is decimated.
        // Beat phase is what the visuals ride on, and it has to advance smoothly.
        if (StepCount % TempoEvery != 0)
        {
            f[FF.Bpm] = (float)_bpm;
            BeatPhase(f);
            return;
        }

        int n = _onsetHist.Length;
        int lagMin = Math.Max(2, (int)Math.Floor(_fmt.FrameRate * 60.0 / MaxBpm));
        int lagMax = Math.Min(n / 2, (int)Math.Ceiling(_fmt.FrameRate * 60.0 / MinBpm));

        // Autocorrelate the onset envelope. Reading through the circular history newest
        // first keeps the most recent audio in every lag's overlap.
        // Autocorrelate the onset envelope with its MEAN REMOVED, normalized by its
        // variance — so each lag is a Pearson correlation coefficient in [-1, 1] rather
        // than a raw product.
        //
        // This is not a refinement, it is what makes confidence mean anything. §7.3's
        // literal "peak / mean(acf)" is tempo-biased: at 180 BPM the search range
        // [31, 94] contains three multiples of the beat period and a 150 ms envelope is
        // half a beat wide, so the mean rises with tempo and the ratio falls. Measured
        // across metronomic click tracks it slid from 3.62 at 60 BPM to 1.28 at 180 —
        // identical rhythmic certainty, a 3x spread in "confidence", and no threshold that
        // could accept 180 without also accepting arrhythmic clicks at 1.20.
        //
        // Removing the mean also fixes the opposite error. Dense material drives the
        // envelope to a near-constant high value, where raw products correlate beautifully
        // at every lag: a music-like signal scored peak/acf0 = 0.985 while its ACF was
        // flat to within 0.2%, i.e. maximum confidence from zero periodicity. As a
        // covariance that same signal has almost no variance to correlate, which is the
        // honest answer.
        double hMean = 0;
        for (int i = 0; i < n; i++) hMean += _onsetHist[i];
        hMean /= n;

        double hVar = 0;
        for (int i = 0; i < n; i++) { double d = _onsetHist[i] - hMean; hVar += d * d; }
        hVar /= n;

        double best = 0, mean = 0, sumSq = 0, bestScore = 0;
        int bestLag = 0;
        int count = 0;
        Span<double> acf = stackalloc double[lagMax + 1];

        if (hVar < 1e-9)
        {
            // A flat envelope carries no rhythm: silence, or a sustained texture with no
            // transients at all. Fall, do not jump — same reasoning as the smoother below.
            _bpmConf += (0 - _bpmConf) * (1 - Math.Exp(-_dt * TempoEvery / ConfFallTau));
            AcfPeak = AcfMean = AcfStd = 0; Acf0 = hVar; AcfLag = 0;
            BeatPhase(f);
            return;
        }

        for (int lag = lagMin; lag <= lagMax; lag++)
        {
            double sum = 0;
            int taps = n - lag;
            for (int i = 0; i < taps; i++)
            {
                int a = (_onsetHistAt - 1 - i + n) % n;
                int b = (_onsetHistAt - 1 - i - lag + n) % n;
                sum += (_onsetHist[a] - hMean) * (_onsetHist[b] - hMean);
            }
            double v = sum / taps / hVar;
            acf[lag] = v;
            mean += v; sumSq += v * v; count++;

            // PERCEPTUAL TEMPO PRIOR, §7.3's octave problem attacked from outside the ACF.
            //
            // The structural objection that kills the harmonic sum does not apply here, and
            // that is the point: any score that is a monotone function of the ACF has the
            // subdivision winning by construction, because acf[lag] IS the peak when the
            // subdivision carries the strongest periodicity. A prior over LAG is an
            // independent term — it does not read the ACF at all — so it can move the choice.
            //
            // Log-normal in BPM, centred on TempoPriorCentre. Listeners have a preferred
            // tapping rate around 100-130 BPM and resolve octave ambiguity toward it, which is
            // the effect being borrowed.
            double scored = v * TempoPrior(lag, _fmt.FrameRate);
            if (scored > bestScore) { bestScore = scored; bestLag = lag; }
            if (v > best) best = v;
        }

        // Confidence still comes from the ACF value at the CHOSEN lag, not from the global
        // peak: the prior may have moved the choice, and reporting the peak's peakiness for a
        // lag the peak is not at would overstate how certain the tracker is.
        if (bestLag > 0) best = acf[bestLag];
        mean = count > 0 ? mean / count : 0;
        double variance = count > 0 ? Math.Max(0, sumSq / count - mean * mean) : 0;
        AcfStd = Math.Sqrt(variance);

        if (bestLag == 0 || best <= 1e-9)
        {
            _bpmConf += (0 - _bpmConf) * (1 - Math.Exp(-_dt * TempoEvery / ConfFallTau));
            BeatPhase(f);
            return;
        }

        // Octave disambiguation, §7.3 — rewritten in Phase 4 against measurement. The
        // version this replaces walked div = 2..4 and took the shorter lag whenever
        // `acf[cand] > acf[chosen] * 0.70`, and it was wrong in two ways that compounded:
        //
        //  1. THE BASELINE MOVED. `chosen` was reassigned inside the loop, so div=3 was
        //     compared against the already-halved candidate rather than against the raw
        //     peak. The effective bar fell 0.70 -> 0.49 -> 0.343 across the three
        //     iterations: a ratchet toward ever-shorter lags.
        //  2. IT COULD ONLY DIVIDE. That is a fatal limitation when the raw ACF peak is
        //     ALREADY on the subdivision, which is the normal case for dense material.
        //     Measured on MusicLike (kick and bass on the beat, hats on the half beat):
        //     at 88 BPM the raw peak sat at lag 33 = 170 BPM, reported with confidence
        //     0.88. High confidence, wrong octave, and no amount of dividing recovers it.
        //     The tell is that 108 BPM tracked correctly only because its subdivision,
        //     216 BPM, falls outside the [60,180] search range.
        //
        // So score candidates on BOTH sides of the raw peak with a harmonic sum: the
        // weighted total of the ACF at a candidate lag and its own multiples. This is the
        // discriminator a single-lag comparison cannot be, because a true fundamental is
        // supported by all of its multiples while a subdivision is supported only by the
        // even ones — the odd half-multiples of the real period land in ACF troughs.
        //
        // Deliberately NOT normalized by the weight sum: a candidate whose multiples fall
        // outside the search range has genuinely less evidence behind it, and dividing that
        // away is what makes a period and twice that period score identically.
        // Static, and the ACF passed in: a local function cannot close over a stackalloc
        // span (CS8175), and heap-allocating the ACF to make it closable would put an
        // allocation on the analysis thread's hot path.
        static double Harmonic(ReadOnlySpan<double> acf, int lag, int lagMax)
        {
            double sum = 0;
            for (int m = 1; m <= 4; m++)
            {
                int l = lag * m;
                if (l > lagMax) break;
                sum += acf[l] / m;
            }
            return sum;
        }

        int chosen = bestLag;
        double harmonicBase = Harmonic(acf, bestLag, lagMax);
        foreach (double ratio in (double[])[0.25, 1.0 / 3, 0.5])
        {
            int cand = (int)Math.Round(bestLag * ratio);
            if (cand < lagMin || cand > lagMax || cand == bestLag) continue;

            // The margin is what keeps a metronomic source stable. Onsets are quantized to
            // hop boundaries and the beat period is rarely a whole number of hops (132 BPM
            // is 42.6), so the ACF at the true fundamental is systematically a little
            // degraded relative to its own multiples. Requiring 8% more harmonic support
            // than the raw peak means a near-tie leaves the peak alone; it is only displaced
            // when the evidence is clear. The BASELINE IS FIXED at the raw peak's score for
            // every candidate — that is defect 1 above, and it is the whole reason this is a
            // scored comparison rather than a sequential walk.
            double s = Harmonic(acf, cand, lagMax);
            if (s > harmonicBase * 1.08 && s > Harmonic(acf, chosen, lagMax)) chosen = cand;
        }

        // The other direction: the raw peak is on the SUBDIVISION and the beat is twice as
        // long. See BeatCombRatio for why no ACF-derived score can detect this, and what
        // can. Always MEASURED so the diagnostics can see it; acted on only under both
        // gates below.
        CombRatio = bestLag * 2 <= lagMax ? BeatCombRatio(bestLag) : 1;

        // GATE 1 — there has to be a periodicity for its octave to be a question at all.
        // On free-time material the weak comb is near zero and the ratio runs away:
        // measured on arrhythmic clicks, p90 199.32, which would spuriously halve the
        // reported tempo of the one population §7.3 most wants left alone. A peaky ACF
        // excludes it, and it is the same population the 0.35 confidence floor excludes
        // downstream — so this gate agrees with that one rather than second-guessing it.
        bool peaky = best > CombMinPeak;

        // GATE 2 — smoothing with hysteresis. The ratio is a 4 s statistic recomputed every
        // TempoEvery hops, and it wobbles across any fixed threshold. A FLAPPING octave
        // decision is worse than either octave on its own: the BPM median then straddles the
        // two and the stability term reads that spread as no tempo at all. Measured with an
        // unsmoothed threshold, MusicLike at 88 BPM landed on the CORRECT octave and
        // confidence still fell from 0.88 to 0.00, which trips the fallback and throws away
        // the lock that was just recovered.
        _combSmooth += ((peaky ? CombRatio : 1) - _combSmooth)
                     * (1 - Math.Exp(-_dt * TempoEvery / CombTau));
        _octaveDoubled = _octaveDoubled ? _combSmooth > CombRelease
                                        : _combSmooth > CombAlternation;

        // MEASURED EVERY HOP, NOT APPLIED. See CombDoublingEnabled.
        //
        // Not applied to a lag the harmonic sum already moved either: the two mechanisms
        // answer the same question, and letting both fire could halve and then double,
        // arriving at an octave neither of them chose.
        if (CombDoublingEnabled && chosen == bestLag && _octaveDoubled && bestLag * 2 <= lagMax)
            chosen = bestLag * 2;

        // Parabolic interpolation around the peak. Without it, lag quantization alone is
        // ~2.6 BPM at 120 BPM — wider than the ±2 BPM the drifting-click-track test
        // allows, so the test would fail on a tracker that is doing everything else right.
        double refined = chosen;
        if (chosen > lagMin && chosen < lagMax)
        {
            double y0 = acf[chosen - 1], y1 = acf[chosen], y2 = acf[chosen + 1];
            double denom = y0 - 2 * y1 + y2;
            if (Math.Abs(denom) > 1e-12)
            {
                double shift = 0.5 * (y0 - y2) / denom;
                if (Math.Abs(shift) <= 1) refined = chosen + shift;
            }
        }

        double instant = 60.0 * _fmt.FrameRate / refined;

        // Accept a little past the nominal range before clamping. The shortest searchable
        // lag is an integer, and at 48 kHz lag 31 works out to 181.5 BPM — so a genuine
        // 180 BPM track produced an estimate just outside the window, every estimate was
        // rejected, and the reported BPM stayed at 0 while confidence sat at 0.89.
        if (instant is >= MinBpm - 6 and <= MaxBpm + 6)
        {
            instant = Math.Clamp(instant, MinBpm, MaxBpm);
            _bpmHist[_bpmHistAt] = (float)instant;
            _bpmHistAt = (_bpmHistAt + 1) % _bpmHist.Length;
            if (_bpmHistCount < _bpmHist.Length) _bpmHistCount++;
        }

        if (_bpmHistCount > 0)
        {
            // 8 s median: the estimate is noisy frame to frame, and a median rides through
            // a few bad frames where an average would be dragged by them.
            _bpmHist.AsSpan(0, _bpmHistCount).CopyTo(_bpmSort);
            Array.Sort(_bpmSort, 0, _bpmHistCount);
            _bpm = _bpmSort[_bpmHistCount / 2];
        }

        AcfPeak = best; AcfMean = mean; AcfLag = chosen; Acf0 = hVar;

        // best is now a correlation coefficient, so the mapping is a straight rescale of a
        // quantity that already means the same thing at every tempo. Measured peaks over
        // 20 s of each signal:
        //
        //   metronomic click tracks, 60 / 90 / 120 / 150 / 180 BPM   0.96 0.93 0.95 0.84 0.89
        //   dense music-like material with hats on the half-beat     0.20
        //   arrhythmic clicks (the Drums -> Space case)              0.07
        //   sustained pad, no transients at all                      0.00
        //
        // The click tracks are flat across the tempo range, which is the property the old
        // peak/mean ratio lacked. The mapping puts §7.3's 0.35 fallback threshold between
        // the arrhythmic and music-like populations: free time falls through to the onset
        // fallback, while dense-but-pulsed material still gets a phase-locked grid.
        // Confidence is peakiness AND stability. Peakiness alone is not enough: over a 4 s
        // window free-time material regularly finds some lag that correlates well, and on
        // arrhythmic clicks that alone reported 0.51 — comfortably above the 0.35 fallback
        // threshold, which would leave the visuals insisting on a grid through Drums ->
        // Space, the exact failure §7.3 exists to prevent.
        //
        // What actually distinguishes a tempo from a coincidence is that it PERSISTS. With
        // a real pulse the frame-to-frame estimate barely moves; in free time it wanders
        // across octaves. So measure the spread of the recent estimates and require it to
        // be small relative to the tempo itself.
        double peakiness = Math.Clamp((best - 0.10) / 0.25, 0, 1);
        double stability = 1;
        if (_bpmHistCount >= 8 && _bpm > 1)
        {
            double mad = 0;
            for (int i = 0; i < _bpmHistCount; i++) mad += Math.Abs(_bpmHist[i] - _bpm);
            mad /= _bpmHistCount;
            // 6% mean absolute deviation is the width of "not a tempo". A click track sits
            // near zero; free time runs well past it.
            stability = Math.Clamp(1 - (mad / _bpm) / 0.06, 0, 1);
        }

        double raw = peakiness * stability;
        double tau = raw > _bpmConf ? ConfRiseTau : ConfFallTau;
        _bpmConf += (raw - _bpmConf) * (1 - Math.Exp(-_dt * TempoEvery / tau));

        f[FF.Bpm] = (float)_bpm;
        BeatPhase(f);
    }

    /// <summary>
    /// Plan §7.3. A phase-locked accumulator nudged by onsets — never reset by them,
    /// because a reset produces visible jitter — and below
    /// <see cref="ConfidenceFloor"/> confidence, driven from the onset impulse instead.
    /// </summary>
    private void BeatPhase(Span<float> f)
    {
        f[FF.BpmConf] = (float)_bpmConf;
        _beatFromOnset = _bpmConf < ConfidenceFloor;

        if (_beatFromOnset)
        {
            // Free time. Freeze the accumulator and let the decaying impulse be the phase:
            // 0 at the hit, rising as it decays. The visuals then pulse when something
            // actually happens instead of sweeping a grid that is not there.
            _beatPhase = 1 - _onsetImpulse;
            f[FF.BeatPhase] = (float)Math.Clamp(_beatPhase, 0, 1);
            f[FF.BarPhase] = (float)Math.Clamp(_beatPhase, 0, 1);
            f[FF.BeatCount] = (float)_beatCount;
            return;
        }

        double advance = _dt * _bpm / 60.0;
        _beatPhase += advance;
        _beatCount += advance;

        // Nudge toward the nearest boundary when an onset lands near one. Gain 0.08 is
        // gentle enough to be invisible frame to frame and still lock within a bar.
        if (_onsetImpulse > 0.95)
        {
            double frac = _beatPhase - Math.Floor(_beatPhase);
            double err = frac < 0.5 ? -frac : 1 - frac;      // signed distance to a boundary
            if (Math.Abs(err) < 0.25)
            {
                _beatPhase += err * 0.08;
                _beatCount += err * 0.08;
            }
        }

        if (_beatPhase >= 1) _beatPhase -= Math.Floor(_beatPhase);

        f[FF.BeatPhase] = (float)_beatPhase;
        f[FF.BeatCount] = (float)_beatCount;
        double bar = _beatCount / 4.0;
        f[FF.BarPhase] = (float)(bar - Math.Floor(bar));
    }

    /* ----------------------------------------------------- §7.4 chroma / key */

    /// <summary>Krumhansl-Schmuckler key profiles, rotated per candidate key.</summary>
    private static readonly double[] MajorProfile =
        [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
    private static readonly double[] MinorProfile =
        [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];

    private void Chroma(Span<float> f)
    {
        Array.Clear(_chroma);

        // Accumulate spectral PEAKS, not every bin, and take each peak's frequency from a
        // parabolic interpolation rather than from its bin index.
        //
        // Summing every bin smears the vector across all twelve pitch classes: a partial's
        // leakage skirt spans several bins, at low frequencies one bin spans several
        // semitones (23.4 Hz per bin against a 7.8 Hz semitone at 130 Hz), and the 1/f
        // taper gives those low bins the most weight. A C major chord came out with C#, G#
        // and A# all at 1.0 and was read as B-flat minor. Peak picking keeps only the
        // partials; interpolation puts them in the right pitch class.
        double maxMag = 0;
        for (int k = 1; k < _bins; k++) if (_mag[k] > maxMag) maxMag = _mag[k];
        double peakFloor = maxMag * 0.02;

        for (int k = 2; k < _bins - 1; k++)
        {
            double m = _mag[k];
            if (m < peakFloor) continue;
            if (m < _mag[k - 1] || m < _mag[k + 1]) continue;      // local maxima only

            // Parabolic interpolation on the three magnitudes: sub-bin frequency, which is
            // what makes the pitch class right in the bottom octaves.
            double denom = _mag[k - 1] - 2 * m + _mag[k + 1];
            double shift = Math.Abs(denom) > 1e-12
                ? Math.Clamp(0.5 * (_mag[k - 1] - _mag[k + 1]) / denom, -0.5, 0.5)
                : 0;
            double hz = (k + shift) * _fmt.BinHz;
            if (hz < ChromaLoHz || hz > ChromaHiHz) continue;

            int semis = (int)Math.Round(12 * Math.Log2(hz / 440.0));
            int pc = ((semis + 9) % 12 + 12) % 12;
            _chroma[pc] += m * (ChromaLoHz / hz);
        }

        // Square-root compression, standard in chroma extraction. Without it one dominant
        // partial — a bass note carries most of the weight once the 1/f taper is applied —
        // turns the vector into a single spike, and a 12-element template correlation
        // against a spike is decided by profile detail rather than by the chord. A C major
        // chord with a bass C detected as B-flat minor at "confidence" 1.000. Compression
        // keeps the tonic dominant while leaving the other chord tones legible.
        for (int pc = 0; pc < 12; pc++) _chroma[pc] = Math.Sqrt(_chroma[pc]);

        double max = 0;
        for (int pc = 0; pc < 12; pc++)
            if (_chroma[pc] > max) max = _chroma[pc];
        if (max > 1e-9)
            for (int pc = 0; pc < 12; pc++) _chroma[pc] /= max;
        else
            Array.Clear(_chroma);

        for (int pc = 0; pc < 12; pc++)
            f[FF.Chroma0 + pc] = (float)_chroma[pc];

        // Key: correlate against 24 rotated templates, take the argmax, and read
        // confidence off the margin to the runner-up.
        double bestScore = double.NegativeInfinity, runnerUp = double.NegativeInfinity;
        int bestPc = _keyPc;
        bool bestMinor = _keyMinor;

        for (int rot = 0; rot < 12; rot++)
        {
            for (int mode = 0; mode < 2; mode++)
            {
                var profile = mode == 0 ? MajorProfile : MinorProfile;
                double score = Correlate(_chroma, profile, rot);
                if (score > bestScore)
                {
                    runnerUp = bestScore;
                    bestScore = score; bestPc = rot; bestMinor = mode == 1;
                }
                else if (score > runnerUp) runnerUp = score;
            }
        }

        if (max > 1e-9)
        {
            _keyPc = bestPc;
            _keyMinor = bestMinor;
            _keyConf = Math.Clamp((bestScore - runnerUp) / 0.25, 0, 1);
        }

        // Circle of fifths, §7.4: musically adjacent keys land on adjacent hues, so a
        // modulation reads as a related colour shift rather than a jump.
        f[FF.KeyHue] = (float)(((_keyPc * 7) % 12) / 12.0);
        f[FF.KeyConf] = (float)_keyConf;

        // chromaFlux: L1 distance from the 500 ms EMA. This is the chord-change detector
        // that fires the rose bloom.
        double emaRate = 1 - Math.Exp(-_dt / 0.5);
        double l1 = 0;
        for (int pc = 0; pc < 12; pc++)
        {
            l1 += Math.Abs(_chroma[pc] - _chromaEma[pc]);
            _chromaEma[pc] += (_chroma[pc] - _chromaEma[pc]) * emaRate;
        }
        // L1 over 12 values in 0..1 tops out at 12, but a real chord change moves four or
        // five of them by most of their range, so 4 is full scale in practice.
        f[FF.ChromaFlux] = (float)Math.Clamp(l1 / 4.0, 0, 1);
    }

    private static double Correlate(double[] chroma, double[] profile, int rotation)
    {
        double mc = 0, mp = 0;
        for (int i = 0; i < 12; i++) { mc += chroma[i]; mp += profile[i]; }
        mc /= 12; mp /= 12;

        double num = 0, dc = 0, dp = 0;
        for (int i = 0; i < 12; i++)
        {
            double c = chroma[i] - mc;
            double p = profile[(i - rotation + 12) % 12] - mp;
            num += c * p; dc += c * c; dp += p * p;
        }
        return num / Math.Sqrt(dc * dp + 1e-12);
    }

    /* ------------------------------------------ §7.5 stereo, perc, novelty */

    /// <summary>
    /// Mid-side, §7.5: cheaper and far more stable than L/R correlation. The sums come from
    /// Levels()'s single pass over the window.
    /// </summary>
    private void Stereo(Span<float> f)
    {
        f[FF.StereoWidth] = (float)Math.Clamp(_rmsSide / (_rmsMid + _rmsSide + 1e-9), 0, 1);
        f[FF.StereoBal] = (float)Math.Clamp((_rmsRight - _rmsLeft) / (_rmsRight + _rmsLeft + 1e-9), -1, 1);
    }

    /// <summary>
    /// HPSS-lite, §7.5. Median across time gives a harmonic estimate (sustained partials
    /// survive a temporal median; transients do not); median across frequency gives a
    /// percussive one (a broadband click survives a spectral median; a narrow partial does
    /// not).
    ///
    /// The two estimates are compared PER BIN and used as a mask, which is what real HPSS
    /// does. Summing the two medians across the spectrum and taking their ratio — the
    /// obvious reading of "perc = sumP / (sumP + sumH)" — does not discriminate at all:
    /// both sums end up dominated by the window's leakage floor, and a sustained pad and a
    /// dense mix both measured 0.42. Per bin, the sign of (p - h) is the interesting part
    /// and the leakage floor cancels out of it.
    /// </summary>
    private void Percussivity(Span<float> f)
    {
        // The spectrogram history must be fed every hop even when the medians are not
        // recomputed — it is what the temporal median is a median OF.
        _mag.AsSpan(0, _hpssBins).CopyTo(_hpss[_hpssAt]);
        _hpssAt = (_hpssAt + 1) % HpssFrames;
        if (_hpssFilled < HpssFrames) _hpssFilled++;

        if (_hpssFilled < HpssFrames || StepCount % PercussivityEvery != 0)
        {
            f[FF.Percussivity] = (float)_percussivity;   // no opinion yet, or not due
            return;
        }

        double sumH = 0, sumP = 0;
        int half = HpssFreqWindow / 2;
        int cur = (_hpssAt - 1 + HpssFrames) % HpssFrames;
        var frame = _hpss[cur];

        for (int k = 0; k < _hpssBins; k += HpssBinStride)
        {
            // Harmonic: median over the last HpssFrames frames at this bin.
            for (int t = 0; t < HpssFrames; t++) _hpssScratch[t] = _hpss[t][k];
            double h = Median(_hpssScratch.AsSpan(0, HpssFrames));

            // Percussive: median over a frequency window of the current frame.
            int lo = Math.Max(0, k - half), hi = Math.Min(_hpssBins - 1, k + half);
            int m = 0;
            for (int j = lo; j <= hi; j++) _hpssScratch[m++] = frame[j];
            double p = Median(_hpssScratch.AsSpan(0, m));

            // Assign this bin's energy to whichever estimate explains it better.
            double e = frame[k];
            if (p > h) sumP += e; else sumH += e;
        }

        // Hold the last value through a gap instead of collapsing to zero. Between two
        // drum hits the spectrum is empty and the ratio is 0/0 — reporting 0 there says
        // "sustained", the opposite of the truth, and it made a click track's median
        // percussivity read 0.000 against a pad's 0.379. Holding also matters for the
        // visuals: percussivity balances the motif and fractal layers, and it should not
        // slam between hits.
        if (sumP + sumH > 1e-6)
        {
            double instant = sumP / (sumP + sumH);
            // 120 ms envelope, stepped by the DECIMATED interval so the time constant is
            // the same whatever PercussivityEvery is set to.
            double dt = _dt * PercussivityEvery;
            _percussivity += (instant - _percussivity) * (1 - Math.Exp(-dt / 0.120));
        }
        HpssP = sumP; HpssH = sumH;
        f[FF.Percussivity] = (float)Math.Clamp(_percussivity, 0, 1);
    }

    /// <summary>Insertion-sort median. The windows are 5 and 17 wide; nothing smarter pays.</summary>
    private static float Median(Span<float> v)
    {
        for (int i = 1; i < v.Length; i++)
        {
            float x = v[i];
            int j = i - 1;
            while (j >= 0 && v[j] > x) { v[j + 1] = v[j]; j--; }
            v[j + 1] = x;
        }
        return v[v.Length / 2];
    }

    private void Novelty(Span<float> f)
    {
        // Cosine distance between the current 8-band vector and its 8 s EMA. Spikes at
        // section boundaries, which is what drives preset crossfades and family changes.
        double rate = 1 - Math.Exp(-_dt / 8.0);
        double dot = 0, na = 0, nb = 0;
        for (int b = 0; b < 8; b++)
        {
            double a = _bandNorm[b], e = _bandEma8s[b];
            dot += a * e; na += a * a; nb += e * e;
            _bandEma8s[b] += (a - e) * rate;
        }
        double cos = (na > 1e-9 && nb > 1e-9) ? dot / Math.Sqrt(na * nb) : 1;

        // Cosine distance between two 8-element vectors that are all non-negative is
        // confined to a small part of 0..1 — both live in the positive octant, so the angle
        // between them is never large. Measured: a hard cut from a bass pad to bright
        // clicks reaches ~0.4, and steady material sits under 0.09.
        //
        // Every other field in the frame spans 0..1, and the presets route novelty
        // expecting that. NoveltyGain rescales so a genuine section change reads as one.
        f[FF.Novelty] = (float)Math.Clamp((1 - cos) * NoveltyGain, 0, 1);
    }

    /* ------------------------------------------------------------- helpers */

    /// <summary>
    /// The §7.1 envelope-plus-window normalizer, for the scalar fields that need the same
    /// treatment as the bands. Same asymmetric attack/release and same fast-open,
    /// slow-close window, so rms and peak behave like a band rather than like raw dB.
    /// </summary>
    /// <param name="spanFloor">
    /// The narrowest window this will divide by. Callers feeding dB want §7.1's 6 dB. A
    /// caller feeding a bare 0..1 ratio needs a floor in that ratio's own units — the 6.0
    /// default is six times the whole range, so it would pin the field near zero forever.
    /// </param>
    /// <param name="envTau">Time constant of the symmetric envelope, when not peak-following.</param>
    /// <param name="windowOnEnvelope">
    /// Track the hi/lo window on the ENVELOPE rather than on the raw input.
    ///
    /// It matters whenever the envelope is much smoother than the input, because then the
    /// numerator is a smoothed value while the denominator is the spread of the raw
    /// extremes, and the field can only ever reach a fraction of its range. Measured on
    /// F_FLUX with a 55 ms symmetric envelope against a raw-extremes window: p90 0.229, so
    /// the top three quarters of 0..1 were unreachable.
    ///
    /// The BANDS must not do this, and that is not an oversight — see the LevelGamma note.
    /// Their envelope is a 10 ms peak follower, close enough to the raw peaks that tracking
    /// the window on it instead pinned every band at 0.79-0.85.
    /// </param>
    private sealed class Normalizer(bool peakFollow = true, double spanFloor = 6.0,
                                    double envTau = 0.150, bool windowOnEnvelope = false)
    {
        private readonly bool _peakFollow = peakFollow;
        private readonly double _spanFloor = spanFloor;
        private readonly double _envTau = envTau;
        private readonly bool _windowOnEnvelope = windowOnEnvelope;
        private double _env, _hi, _lo;
        private bool _primed;

        public void Reset() { _env = _hi = _lo = -120; _primed = false; }

        public double Step(double value, double dt)
        {
            if (!_primed) { _env = _hi = _lo = value; _primed = true; }

            if (_peakFollow)
            {
                // §7.1's asymmetric envelope: 10 ms attack so hits snap, 250 ms release so
                // the decay is graceful.
                double attack = 1 - Math.Exp(-dt / 0.010);
                double release = 1 - Math.Exp(-dt / 0.250);
                _env += (value - _env) * (value > _env ? attack : release);
            }
            else
            {
                // Symmetric, for quantities that are not levels. A peak follower on a crest
                // factor or a spectral centroid tracks its maximum, not its value, and both
                // then sit at the top of their own window permanently.
                _env += (value - _env) * (1 - Math.Exp(-dt / _envTau));
            }

            double w = _windowOnEnvelope ? _env : value;
            _hi = w > _hi ? w : _hi + (w - _hi) * (1 - Math.Exp(-dt / 3.0));
            _lo = w < _lo ? w : _lo + (w - _lo) * (1 - Math.Exp(-dt / 8.0));

            // The clamp comes LAST, after the affine map onto the observed window.
            // Clamping against a threshold first and calling the result normalized is
            // exactly what made F_FLUX binary — see Onset().
            return Math.Clamp((_env - _lo) / Math.Max(_hi - _lo, _spanFloor), 0, 1);
        }
    }
}
