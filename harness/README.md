# Ripple — Phase 0 harness

`harness/index.html` is the visual harness: one self-contained file that runs the complete
render pipeline plus the preset-authoring UI. Open it in a browser. No build step, no server
(a `file://` open works; the mic needs `localhost` or `https` because of browser policy).

Scope is plan §5 (Feature Frame), §8 (visual system), §10 (preset schema) and §12 (safety
limiter). Audio capture, tempo tracking and SMTC are the native host's job.

---

## Panes

**Canvas** — full-bleed. `D` toggles the diagnostic HUD, `Tab` collapses the panel,
`←`/`→` step presets, `[`/`]` master intensity, `R` arms the timeline recorder, `M` mic.

**Panel** — lab instrumentation. Dense, monospaced, one accent colour. Every value is a
labelled readout next to the control that changes it. Fields marked `~` are approximated in
Phase 0 (see below).

---

## Pipeline (plan §8.1)

```
ping/pong FBO, RGBA16F, at renderScale (0.75x default)
  A  warp        sample the previous frame at a displaced UV
                 (rotation + zoom + fBm domain warp + curl, modulated by
                  sub/bass and stereo width), multiplied by decay
  B  paint       additive: fractal layer -> rose -> bolt -> particles
  C  composite   tonemap -> bloom -> saturation -> grain -> vignette
                 -> safety clamp -> full-res backbuffer
```

Three things about this are worth knowing before you tune it.

**Feedback decay is the primary axis, and `paintGain` is normalized by `(1 - decay)`.**
The buffer's equilibrium is `paint / (1 - decay)`, so folding `(1 - decay)` into the paint
cancels it: moving decay changes the *look* — trail length, self-similarity — without
changing exposure. Equilibrium luminance is roughly `paintGain × 30`.

**The paint layer is deliberately sparse and high contrast.** A paint pass that covers the
frame evenly accumulates to a flat wash at equilibrium no matter how it is scaled. `warp_fbm`
paints a dim body and a hot, thin meniscus, and roughly a third of the frame is masked to
black — so the trail becomes ribbons instead of fog. If you brighten the body, expect to lose
the liquid read.

**Colour survives only inside a narrow palette window.** The trail sums about `1/(1-decay)`
palette samples; a wide lookup range averages them to grey however saturated each one was.
`warp_fbm` drives the palette from the *lowest*-frequency field it has, over a window of
about 0.22 of the ramp, with per-cell tinting on top. Key response rotates the palette rather
than blending toward a raw hue, for the same reason.

---

## Input modes

1. **SYNTH — the primary mode.** A slider and an optional LFO (rate / depth / sine·tri·saw·sqr·rnd)
   for every Feature Frame field. `JAM` restores a default LFO set; `AUTO RHYTHM` advances beat
   and bar phase from the BPM slider, fires onsets on beats, and spikes `chromaFlux` about every
   second bar so the rose bloom is testable with no audio at all.
2. **MIC** — live microphone.
3. **FILE** — an uploaded audio file (monitored to the speakers).
4. **REPLAY** — a recorded feature timeline.

### What is faithful and what is approximate

Implemented exactly as specified, because a divergence here would invalidate every preset
tuned in this harness:

- **§7.1 adaptive per-band normalization** — 8 perceptual bands with edges derived from the
  actual `AudioContext.sampleRate`, per-band `E = Σ|X|²/binCount` → dB, asymmetric 10 ms
  attack / 250 ms release envelopes, 3 s hi / 8 s lo tracking window, 6 dB floor.
  `AnalyserNode.smoothingTimeConstant` is set to 0 so those envelopes are the only smoothing.
- **§7.2 spectral-flux onsets** — flux over 40 Hz–8 kHz normalized by the frame's magnitude
  sum, peak-picked against `median(last 43) × 1.4 + 0.02`, 50 ms refractory, 150 ms impulse decay.
- **§7.5 stereo width** via mid/side RMS, and **novelty** as cosine distance of the 8-band
  vector against its 8 s EMA.
- **§7.3 beat phase** as a phase-locked accumulator with onset nudge at gain 0.08, including
  the `bpmConfidence < 0.35` fallback that drives beat phase from the decaying onset impulse.

Approximated, and flagged with `~` in the panel:

- **BPM / confidence** — slider or LFO driven. No autocorrelation (plan §7.3 is Phase 2).
- **Chroma / key** — bin→pitch-class accumulation with a log-frequency taper and argmax
  mapped around the circle of fifths, not the 24-template correlation of §7.4. `chromaFlux`
  itself (L1 distance to a 500 ms EMA) is exact, so the rose fires correctly.
- **Percussivity** — high/low flux ratio, not the HPSS median filtering of §7.5.

---

## Feature timeline (the Phase 1 parity gate)

`FEATURE TIMELINE` records the Feature Frame at a chosen rate for N seconds and exports:

```json
{ "kind": "ripple-feature-timeline", "rate": 60, "seconds": 20,
  "frameBytes": 224, "fieldOrder": [ … 56 names … ],
  "frames": [ [ …56 floats… ], … ] }
```

`frames[i]` is the 56-float std140 `FeatureFrame` in declaration order. Replay one row per
frame, upload verbatim, and both hosts see an identical timeline — which is what makes the
plan §15 phase-1 frame diff executable rather than aspirational. `LOAD` + `PLAY` replays a
timeline in the harness; the host still owns `uTime` and `uRes`, exactly as the native player
will.

---

## Shader extraction (CONTRACTS §3)

Every shader lives in its own template literal in `harness/index.html`, fenced with
`// ===== FILE: shaders/… =====`. The `FILE:` path is the authoritative destination.

```
node tools/extract-shaders.mjs                 # write shaders/
node tools/extract-shaders.mjs --check         # CI: fail if shaders/ has drifted
```

The tool refuses to write a file containing a `#version` directive, and refuses duplicate
destinations. The harness loads its shaders through the same flat `#include` resolver with
cycle detection that the native host must implement — it does not inline includes by hand.

**One sanctioned divergence** (CONTRACTS §2), and it is the only one: GLSL ES 3.00 has no
`binding` layout qualifier on uniform blocks — that arrives in ES 3.10 / desktop GL 4.2. So
`common/features.glsl` carries

```glsl
#ifdef RIPPLE_WEBGL
layout(std140) uniform FeatureFrame {
#else
layout(std140, binding = 0) uniform FeatureFrame {
#endif
```

and the harness calls `gl.uniformBlockBinding(prog, idx, 0)` at link time, which is identical
in effect. The file stays byte-identical across hosts. `RIPPLE_WEBGL` is defined in the WebGL2
preamble only. Nothing else in the tree is guarded.

---

## Preset schema

`schemaVersion 1`, per plan §10. The eight uploaded era presets load **unchanged**; the
harness adds optional keys with defaults, all additive, so an era preset that omits them is
still valid and a native host that ignores them still renders. Full list of additions:

| block | keys |
|---|---|
| `palette` | `spiral` — tie-dye spiral amount, applied to the palette *lookup* |
| `feedback` | `symmetry`, `paintGain`, `centerPull`, `onsetDecay` |
| `fractalParams` | `warp`, `scale`, `flow`, `cells`, `rim`, `rimWidth`, `iridescence`, `mask`, `octaves`, `trapGain`, `zoomPeriod`, `zoomDepth`, `fold`, `line` |
| `particles` | `size`, `rise` |
| `post` | `exposure`, `saturation`, `bloomTaps`, `bloomKnee` |

Merge order is CONTRACTS §4: era preset → song-mode override → user config → governor clamp.
Overrides are partial and merge recursively (`{"feedback":{"decay":0.995}}` changes only the
decay). The `SONG MODE` dropdown exercises that path against `data/songs.json`.

`EXPORT JSON` downloads the working preset; `IMPORT` accepts either a preset or a feature
timeline and routes on `kind`.

### Fractal families

`warp_fbm` (hero), `julia_orbit`, `apollonian`, `mandel_zoom`. `kleinian`, `ifs_flame` and
`mandelbulb` are named in the plan and in `songs.json` but are Phase 1+; requests for them
fall back to `warp_fbm` and the panel says so, rather than failing.

### Authored presets

`presets/authored/` — tuned here against the real pipeline, and the numbers differ
substantially from the era starting points, as expected:

| preset | family | character |
|---|---|---|
| `oil-wheel` | `warp_fbm` | the reference look — long decay, thin bright meniscus, magenta/amber/navy pigments |
| `fillmore-poster` | `warp_fbm` | short decay, kaleidoscopic fold, Day-Glo, heavy spiral |
| `dark-star-drift` | `warp_fbm` | 0.9955 decay, 6 octaves, almost still — for free-time passages |
| `scarlet-lace` | `apollonian` | geometric lace against ember palette, rose foregrounded |
| `beam-julia` | `julia_orbit` | beat-morphing `c`, symmetry from stereo width, hot orbit traps |

---

## Safety limiter (plan §12) — unconditional

Runs in `composite.frag` via `common/safety.glsl` on every frame, in every mode.

- **Luminance rate cap.** Mean frame luminance is measured from the final buffer's own mip
  chain (`generateMipmap`, then read the 1×1 top level — 4 bytes of readback, no full-res
  stall). A closed-loop gain on the host clamps the frame-to-frame delta; it attacks
  instantly and releases slowly.
- **No full-field flashes above 3 Hz.** Sign reversals of the luminance derivative are counted
  over a 1 s window; above 3 Hz the gain is pulled hard.
- **Brightness ceiling** — never full white.
- **Red↔blue guard.** As the palette rotation rate climbs, saturation is bled out of the R/B
  axis so the interpolation path cannot swing between those poles at speed.

A preset may be **stricter** than the floor and never looser: `maxLuminanceDelta` is clamped
to ≤ 0.12 and `maxBrightness` to ≤ 0.92 on the host *before* upload, and the same ceilings are
`#define`d in `safety.glsl`. The `SAFETY LIMITER` section shows the live loop — mean
luminance, frame delta, gain, flash rate, effective ceiling.

---

## Performance governor

Plan §11's dials, pulled in the specified order — `renderScale` → particle count → fractal
octaves → bloom taps — with asymmetric hysteresis (30 frames down, 300 up, 2 s dwell). Two
additions a browser needs and the native host will not:

- A **warm-up probe** at startup: one synchronous hero-shader draw into a 128×72 target with
  an explicit `finish()` gives a real per-pixel cost, so the harness opens at a tier the
  machine can hold instead of discovering it by stalling on frame one.
- The controller reads the **rAF interval**, not wall-clock time around the draw calls. GL
  submission is asynchronous, so timing the draw measures nothing useful.

Presets are therefore authored against the same quality ladder the native host will use. Turn
`GOVERNOR` off to pin `renderScale` by hand.

The harness also falls back to a timer when `requestAnimationFrame` is suspended (embedded
preview panes report `document.hidden`), and hands control straight back to rAF when it wakes.
Plan §11 wants an occluded window to stop rendering entirely — that is correct for the shipped
app and is the native host's behaviour, not the harness's.

---

## Handing off to Phase 1

1. `node tools/extract-shaders.mjs` — `shaders/` on disk, no `#version` lines.
2. Implement the same preamble injector and `#include` resolver in `ShaderLoader.cs`.
3. `Debug.Assert(Marshal.SizeOf<FeatureFrame>() == 224)`; bind the UBO at point 0; zero-fill
   `uReserved`.
4. Port the synthetic feature driver, then record a timeline here and replay it there. Diff
   the frames. That is gate 1.
