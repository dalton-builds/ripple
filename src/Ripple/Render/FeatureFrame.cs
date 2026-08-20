using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ripple.Render;

/// <summary>
/// The Feature Frame UBO, mirrored from <c>shaders/common/features.glsl</c>.
/// FROZEN CONTRACT, schemaVersion 1 — see CONTRACTS.md §1.
///
/// Every member is a <see cref="Vector4"/> on purpose: std140 pads float[] and
/// vec3 to a 16-byte stride, which is the single most common cause of silent
/// uniform-buffer corruption. Packing as vec4 removes that entire class of bug
/// and makes the C# layout trivially identical to the GLSL one.
///
/// 14 vec4 = 224 bytes. Asserted by Ripple.Tests.FeatureFrameTests, which also
/// round-trips a known frame through a real GL context.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FeatureFrame
{
    public Vector4 Time;      // x t  y dt  z trackPos  w trackFrac
    public Vector4 Level;     // x rms  y peak  z loudSlow  w dynamics
    public Vector4 Bands0;    // sub bass lowMid mid
    public Vector4 Bands1;    // hiMid presence brilliance air
    public Vector4 Rhythm;    // flux onset beatPhase barPhase
    public Vector4 Tempo;     // bpm(RAW) bpmConf beatCount(RAW) percussivity
    public Vector4 Chroma0;   // C C# D D#
    public Vector4 Chroma1;   // E F F# G
    public Vector4 Chroma2;   // G# A A# B
    public Vector4 Tonal;     // keyHue keyConf chromaFlux centroid
    public Vector4 Space;     // stereoWidth stereoBal(RAW) novelty sectionPhase
    public Vector4 Ctl;       // intensity era(RAW) songMode(RAW) paletteMix
    public Vector4 Res;       // width(RAW) height(RAW) aspect(RAW) renderScale
    public Vector4 Reserved;  // schemaVersion 2+ growth. MUST stay zero-filled.

    /// <summary>56 floats. The timeline format records exactly this many per row.</summary>
    public const int FloatCount = 56;

    /// <summary>224 bytes. Both hosts assert this; CONTRACTS.md §1.</summary>
    public const int ByteSize = FloatCount * 4;

    /// <summary>
    /// The frame as a flat float span, in declaration order. This is the view
    /// the whole host uses, because the harness's Float32Array is indexed the
    /// same way — keeping the two ports line-for-line comparable.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<float> AsSpan()
        => MemoryMarshal.CreateSpan(ref Unsafe.As<FeatureFrame, float>(ref this), FloatCount);
}

/// <summary>
/// Float indices into the frame. These mirror the <c>FIELDS</c> table in the
/// harness one for one; the timeline's <c>fieldOrder</c> array is this list.
/// </summary>
public static class FF
{
    public const int Time = 0, Dt = 1, TrackPos = 2, TrackFrac = 3;
    public const int Rms = 4, Peak = 5, LoudSlow = 6, Dynamics = 7;
    public const int Sub = 8, Bass = 9, LowMid = 10, Mid = 11;
    public const int HiMid = 12, Presence = 13, Brilliance = 14, Air = 15;
    public const int Flux = 16, Onset = 17, BeatPhase = 18, BarPhase = 19;
    public const int Bpm = 20, BpmConf = 21, BeatCount = 22, Percussivity = 23;
    public const int Chroma0 = 24;   // 24..35
    public const int KeyHue = 36, KeyConf = 37, ChromaFlux = 38, Centroid = 39;
    public const int StereoWidth = 40, StereoBal = 41, Novelty = 42, SectionPhase = 43;
    public const int Intensity = 44, Era = 45, SongMode = 46, PaletteMix = 47;
    public const int Width = 48, Height = 49, Aspect = 50, RenderScale = 51;
    public const int Reserved = 52;  // 52..55

    /// <summary>
    /// Indices the HOST owns on replay. Everything else in a timeline row is
    /// uploaded verbatim — see the note tlExport() writes into the JSON.
    /// </summary>
    public static ReadOnlySpan<int> HostOwned => [Time, Dt, Width, Height, Aspect, RenderScale];

    /// <summary>Field names in declaration order, matching timeline `fieldOrder`.</summary>
    public static readonly string[] Names =
    [
        "time", "dt", "trackPos", "trackFrac",
        "rms", "peak", "loudSlow", "dynamics",
        "sub", "bass", "lowMid", "mid", "hiMid", "presence", "brilliance", "air",
        "flux", "onset", "beatPhase", "barPhase",
        "bpm", "bpmConf", "beatCount", "percussivity",
        "ch0", "ch1", "ch2", "ch3", "ch4", "ch5", "ch6", "ch7", "ch8", "ch9", "ch10", "ch11",
        "keyHue", "keyConf", "chromaFlux", "centroid",
        "stereoWidth", "stereoBal", "novelty", "sectionPhase",
        "intensity", "era", "songMode", "paletteMix",
        "width", "height", "aspect", "renderScale",
        "_r0", "_r1", "_r2", "_r3",
    ];
}
