// ============================================================================
//  Ripple — Feature Frame
//  FROZEN CONTRACT. schemaVersion 1.
// ============================================================================
//  This file is shared VERBATIM between the WebGL2 harness and the native
//  OpenGL host. Do not fork it. Do not add a #version line — each host
//  injects its own preamble at load time (see ShaderLoader / harness loader).
//
//  Every member is vec4-aligned DELIBERATELY. std140 pads float[] and vec3 to
//  a 16-byte stride, which is the single most common cause of silent uniform
//  buffer corruption. Packing everything as vec4 eliminates that entire class
//  of bug. Total size: 224 bytes (14 vec4). Assert this on the C# side.
//
//  All fields are normalized 0..1 EXCEPT where marked RAW.
//  Normalization lives in the DSP, never in the shader — that is what makes
//  presets portable and lets the synthetic driver produce valid states.
// ----------------------------------------------------------------------------
//  ONE sanctioned divergence, per CONTRACTS.md §2. GLSL ES 3.00 (WebGL2) does
//  not accept the `binding` layout qualifier on a uniform block — it arrives
//  in ES 3.10 / desktop GL 4.2. The harness therefore declares the block
//  without it and calls glUniformBlockBinding(prog, idx, 0) at link time,
//  which is bit-identical in effect. Guarded rather than forked so the file
//  stays byte-identical across hosts.
// ============================================================================

#ifdef RIPPLE_WEBGL
layout(std140) uniform FeatureFrame {
#else
layout(std140, binding = 0) uniform FeatureFrame {
#endif
    vec4 uTime;      // x: seconds since start        y: dt (seconds)
                     // z: track position (seconds)   w: track fraction (0 if unknown)

    vec4 uLevel;     // x: rms                        y: peak
                     // z: slow loudness (3s EMA)     w: dynamics (peak - rms)

    vec4 uBands[2];  // 8 perceptual bands, adaptively normalized (see plan 7.1)
                     // [0].x sub 20-60      [0].y bass 60-150
                     // [0].z lowMid 150-400 [0].w mid 400-1k
                     // [1].x hiMid 1k-2.4k  [1].y presence 2.4k-5k
                     // [1].z brilliance 5k-10k [1].w air 10k-16k

    vec4 uRhythm;    // x: spectral flux              y: onset impulse (~150ms decay)
                     // z: beat phase 0..1            w: bar phase 0..1 (4 beats)

    vec4 uTempo;     // x: BPM 40..200 (RAW)          y: BPM confidence
                     // z: beat counter (RAW, monotonic)  w: percussivity

    vec4 uChroma[3]; // 12 pitch-class energies, C at [0].x, normalized to max=1
                     // [0] = C  C# D  D#   [1] = E  F  F# G   [2] = G# A  A# B

    vec4 uTonal;     // x: key hue 0..1 (circle of fifths)  y: key confidence
                     // z: chroma flux (chord change)       w: spectral centroid

    vec4 uSpace;     // x: stereo width               y: stereo balance -1..1 (RAW)
                     // z: novelty (structural change) w: section phase

    vec4 uCtl;       // x: master intensity           y: era id 0..7 (RAW)
                     // z: song mode (RAW)            w: palette crossfade 0..1

    vec4 uRes;       // x: width px (RAW)             y: height px (RAW)
                     // z: aspect (RAW)               w: render scale

    vec4 uReserved;  // Reserved for schemaVersion 2+. MUST be zero-filled by
                     // both hosts. Exists so new fields can be added without
                     // repacking or changing the buffer size — append here,
                     // never reorder the blocks above.
};

// Size check: 13 declared vec4 above (uBands=2, uChroma=3) + uReserved = 14.
// 14 * 16 = 224 bytes. Both hosts assert this.

// ---------------------------------------------------------------------------
//  Named accessors. USE THESE, not the raw swizzles — they are the stable API.
//  If the packing ever has to change, only this block changes.
// ---------------------------------------------------------------------------

#define F_TIME          uTime.x
#define F_DT            uTime.y
#define F_TRACK_POS     uTime.z
#define F_TRACK_FRAC    uTime.w

#define F_RMS           uLevel.x
#define F_PEAK          uLevel.y
#define F_LOUD_SLOW     uLevel.z
#define F_DYNAMICS      uLevel.w

#define F_SUB           uBands[0].x
#define F_BASS          uBands[0].y
#define F_LOWMID        uBands[0].z
#define F_MID           uBands[0].w
#define F_HIMID         uBands[1].x
#define F_PRESENCE      uBands[1].y
#define F_BRILLIANCE    uBands[1].z
#define F_AIR           uBands[1].w

#define F_FLUX          uRhythm.x
#define F_ONSET         uRhythm.y
#define F_BEAT_PHASE    uRhythm.z
#define F_BAR_PHASE     uRhythm.w

#define F_BPM           uTempo.x
#define F_BPM_CONF      uTempo.y
#define F_BEAT_COUNT    uTempo.z
#define F_PERCUSSIVITY  uTempo.w

#define F_KEY_HUE       uTonal.x
#define F_KEY_CONF      uTonal.y
#define F_CHROMA_FLUX   uTonal.z
#define F_CENTROID      uTonal.w

#define F_STEREO_WIDTH  uSpace.x
#define F_STEREO_BAL    uSpace.y
#define F_NOVELTY       uSpace.z
#define F_SECTION_PHASE uSpace.w

#define F_INTENSITY     uCtl.x
#define F_ERA           uCtl.y
#define F_SONG_MODE     uCtl.z
#define F_PALETTE_MIX   uCtl.w

#define F_ASPECT        uRes.z
#define F_RENDER_SCALE  uRes.w

// Chroma is packed across three vec4s; index it through this.
float chroma(int pc) {
    int v = pc >> 2;
    int c = pc & 3;
    vec4 q = (v == 0) ? uChroma[0] : ((v == 1) ? uChroma[1] : uChroma[2]);
    return (c == 0) ? q.x : ((c == 1) ? q.y : ((c == 2) ? q.z : q.w));
}

// Beat-synced ramps. Prefer these over raw time for anything rhythmic —
// they degrade correctly when BPM confidence collapses (free-time passages),
// because the DSP drives F_BEAT_PHASE from onset decay in that case.
float beatPulse(float sharpness) {
    return pow(1.0 - F_BEAT_PHASE, sharpness);
}
float barPulse(float sharpness) {
    return pow(1.0 - F_BAR_PHASE, sharpness);
}

// Overall band energy, useful as a cheap global drive.
float bandSum() {
    return (uBands[0].x + uBands[0].y + uBands[0].z + uBands[0].w
          + uBands[1].x + uBands[1].y + uBands[1].z + uBands[1].w) * 0.125;
}
