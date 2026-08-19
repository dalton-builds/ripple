# Ripple — Implementation Plan

A lightweight Windows desktop music visualizer that reacts to Spotify audio, blending fractal
geometry with liquid-light-show and Grateful Dead visual idiom. Built to be a talking point for
people walking past a desk, and cheap enough to leave running under a full architecture workload.

**Project codename:** `ripple` (double meaning — the warp pass literally ripples the feedback
buffer). Rename freely; the name appears only in `AssemblyName` and the config path.

**Status:** plan ready for execution. Phases 0–5 are the build. Phase 6 is optional.

---

## 1. Objective and success criteria

Three requirements, all of which must be measurable or they will quietly get dropped:

| # | Requirement | Measurable form |
|---|---|---|
| R1 | Reacts to Spotify desktop audio | Visual response to a transient within 60 ms; no OAuth, no API key, no account requirement |
| R2 | Talking point | A stranger walking past stops. Concretely: visible per-song visual identity shifts, an on-screen now-playing caption with show date when available, and a togglable BPM/key readout that reveals the machine is *analyzing* rather than just wiggling |
| R3 | Lightweight | ≤ 60 MB working set, ≤ 2% of one CPU core average, ≤ 8% GPU at 960×540/60 fps on a modern dGPU, zero heap allocation per frame in the render loop, near-zero cost when audio is silent or the window is hidden |

R3 is the requirement that kills projects like this. It is enforced in Phase 4 by an automated
budget test, not by good intentions. See §11.

---

## 2. Verified constraints — read this before designing anything

These were checked against current sources (August 2026). Two of them invalidate the obvious
approach, so do not re-derive the design from first principles and quietly reintroduce them.

### 2.1 Spotify's audio analysis API is gone. Do not build on it.

Spotify deprecated `GET /v1/audio-features` and `GET /v1/audio-analysis` on **27 November 2024**.
Only apps that already held (or had pending) an extended quota grant before that date still work;
everything else receives 403, there is no replacement endpoint, and there is no application path
for a new personal project.

**Consequence:** all beat, tempo, key, and energy detection must be computed **locally from the
audio signal**. This is not a downgrade — local DSP gives ~10 ms latency instead of a
pre-computed static beat grid, which matters enormously for live Grateful Dead recordings where
tempo drifts continuously. The whole design leans into this.

### 2.2 The Spotify Web API is a bad dependency generally.

Further tightening in **February 2026** restricted Development Mode apps to one client ID per
developer, five allowlisted users, a reduced endpoint set, and — critically — **an active Spotify
Premium subscription on the app owner's account, or the app stops working**. `GET
/me/player/currently-playing` does still exist, but the dependency chain (OAuth PKCE flow, token
refresh, redirect URI on `127.0.0.1`, Premium requirement, ongoing policy churn) is absurd
overhead for reading a song title on the same machine that is playing it.

**Consequence:** get now-playing metadata from **Windows SMTC**
(`Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager`) instead. This reads
Spotify's own media session locally — title, artist, album, thumbnail, playback position, play
state — with zero network, zero auth, zero API keys, and no Premium requirement. Filter to Spotify
specifically via `Session.SourceAppUserModelId`.

The Web API is a **non-goal**. If richer metadata is ever wanted (canonical album year), add it as
an opt-in enrichment behind a feature flag, never as a load-bearing path.

### 2.3 Audio capture: WASAPI loopback on the default render endpoint.

Spotify's Windows client does not use a protected audio path. Standard WASAPI loopback capture on
the default render endpoint captures it fine (this is how OBS captures Spotify). Use
`NAudio.Wave.WasapiLoopbackCapture`.

**Known limitations to handle explicitly, not discover later:**

- Loopback captures *everything* the system plays — Teams notification dings, browser video. Accept
  this in v1; §16 covers per-process isolation as an optional Phase 6.
- **Loopback may deliver no callbacks at all when the endpoint is idle** on some Windows versions,
  and silence buffers on others. Never drive the render loop from audio callbacks. Treat stale
  audio as silence via a timestamp check.
- **Default device changes** (headphones plugged in, Bluetooth connect) kill the capture stream
  silently. Register for `MMDeviceEnumerator` notifications *and* run a watchdog: if no audio data
  for 2 s while SMTC reports `Playing`, tear down and re-initialize capture.
- Device format varies — 44.1/48/96 kHz, and usually 32-bit IEEE float but sometimes 16-bit PCM.
  Read `WaveFormat` at runtime; derive all FFT bin frequencies from the actual sample rate. Do not
  hardcode 48000.
- If another app holds the endpoint in **WASAPI exclusive mode**, loopback fails. Detect, surface a
  one-line status message, keep running in idle.

---

## 3. Stack decision

**Chosen: C# / .NET 10 LTS (`net10.0-windows10.0.19041.0`) + Silk.NET (OpenGL 4.3 core) + NAudio + Windows.Media.Control**

> **Version note.** Target **.NET 10**, not .NET 8. Both .NET 8 (LTS) and .NET 9 (STS) reach end of
> support on **10 November 2026** — roughly three months from now — while .NET 10 LTS is supported
> through November 2028. Starting a new project on an EOL-in-90-days runtime is a pointless
> migration debt.

The decisive factor is not language preference — it is that **the visual prototype and the shipped
app must run the same shader source**. Shader art is a visual-iteration problem: it is enormously
faster to tune in a browser with instant reload than in a compile-run loop. But a rewrite between
prototype and product throws away the tuning and reintroduces every bug. Choosing a native stack
that consumes **GLSL directly** makes the port a copy of the files plus a version preamble.

| Concern | How this stack answers it |
|---|---|
| Same shaders as the browser prototype | Desktop GLSL 330 core vs WebGL2's GLSL ES 300 differ only in the `#version` line and precision qualifiers. A 20-line preamble injector makes the shader files byte-identical. |
| Audio capture | `WasapiLoopbackCapture` is ~5 lines to float32 stereo. Battle-tested. |
| Now-playing metadata | `Windows.Media.Control` is directly available from .NET with the `net10.0-windows10.0.19041.0` TFM — no interop layer. `Dubya.WindowsMediaController` (NuGet) wraps per-app session events if the raw async API proves fiddly. |
| Footprint | Self-contained trimmed ReadyToRun publish → single exe, working set comfortably under 60 MB. |
| Iteration speed in Claude Code | One `dotnet run`. Shader and preset hot-reload from disk means most tuning needs no rebuild at all. |

**Do not use NativeAOT.** CsWinRT projection plus AOT trimming is a known source of obscure
runtime failures, and the footprint win over trimmed self-contained R2R is not worth it here. If a
future pass wants AOT, isolate all WinRT access behind an interface first.

### Rejected alternatives, with reasons

- **Electron / Tauri + WebGL.** Fastest path to the visuals, but Electron's Chromium baseline
  (150–250 MB) directly violates R3, which is a stated hard requirement. Tauri (WebView2) is
  lighter but WebView2 audio-loopback and low-latency uniform upload paths are worse, and the
  process still isn't cheap. Rejected on footprint.
- **Rust + wgpu.** Genuinely excellent and produces the smallest binary. Rejected because WGSL
  requires porting every shader from the browser prototype, which is exactly the cost this design
  is built to avoid. (Rust + `glow` + `wasapi-rs` keeps GLSL and is a legitimate alternative —
  choose it if a smaller binary matters more than iteration speed, and verify `wasapi-rs` loopback
  behavior first.)
- **Native C++ / D3D11 + HLSL.** Lowest footprint, but the slowest iteration loop and an HLSL port.
  Only worth it if the CPU budget were an order of magnitude tighter than it is.
- **Python + moderngl.** Fast to prototype, but GIL contention on the DSP thread, ~100 MB
  interpreter baseline, and PyInstaller packaging pain. Rejected.

---

## 4. Build split: Claude Design and Claude Code

Both are used, for genuinely different jobs. The split is what makes the schedule work.

### Claude Design → Phase 0: the visual harness

Build a single-file WebGL2 harness that runs the **complete shader pipeline** (feedback, warp,
fractal, motifs, particles, post) plus a control panel. This is where the look gets decided.

The critical design move: the harness can be driven by a **synthetic feature driver** — sliders and
LFOs for every field in the Feature Frame (§5) — as well as by real mic/file audio. This decouples
"make it beautiful" from "make audio capture work," so visual tuning can happen before a single
line of C# exists, and later reproduces any audio condition on demand.

The harness is **not a throwaway**. It becomes the permanent preset-authoring tool: it exports
preset JSON that the native app loads directly. Design its UI accordingly — read
`/mnt/skills/public/frontend-design/SKILL.md` before building the chrome. The panel should look
like lab equipment, not a web app: dense, monospaced, legible in a dark room. Do not spend the
aesthetic budget on the panel; the canvas is the hero.

### Claude Code → Phases 1–5: the native application

Window and GL context, DSP, WASAPI capture, SMTC integration, era/song intelligence, performance
governor, safety limiter, tray and hotkeys, packaging, tests.

### The bridge: two shared contracts

Both sides implement the same **Feature Frame UBO** (§5) and the same **preset JSON schema** (§10).
Same uniform names, same numeric ranges, same shader files. Get these right in Phase 0 and the port
is mechanical. Get them wrong and Phase 1 becomes a rewrite. Version both with an integer
`schemaVersion` and fail loudly on mismatch.

---

## 5. The Feature Frame contract (keystone — implement first)

One UBO, uploaded once per frame. Everything the visuals know about the music comes through here.

Every field is normalized to **0..1** unless the comment says otherwise. Normalization lives in the
DSP, never in the shader — this is what lets presets be portable and lets the synthetic driver
produce visually valid states.

```glsl
// std140. ALL members are vec4-aligned deliberately: std140 pads float[] and vec3
// to a 16-byte stride, which is the single most common source of silent
// uniform-buffer corruption. Packing as vec4 avoids the entire class of bug.
layout(std140, binding = 0) uniform FeatureFrame {
    vec4 uTime;     // x: seconds since start   y: dt (s)
                    // z: track position (s)    w: track fraction 0..1 (0 if unknown)

    vec4 uLevel;    // x: rms  y: peak  z: slow loudness (3 s EMA)  w: dynamics (peak-rms)

    vec4 uBands[2]; // 8 perceptual bands, adaptively normalized:
                    // [0] = sub, bass, lowMid, mid
                    // [1] = hiMid, presence, brilliance, air

    vec4 uRhythm;   // x: spectral flux        y: onset impulse (decays over ~150 ms)
                    // z: beat phase 0..1      w: bar phase 0..1 (4 beats)

    vec4 uTempo;    // x: BPM (40..200, RAW UNITS)  y: BPM confidence
                    // z: beat counter (monotonic)   w: percussivity

    vec4 uChroma[3];// 12 pitch-class energies, C at [0].x, normalized to max=1

    vec4 uTonal;    // x: key hue 0..1 (circle of fifths)  y: key confidence
                    // z: chroma flux (chord change)       w: spectral centroid

    vec4 uSpace;    // x: stereo width  y: stereo balance (-1..1)
                    // z: novelty (structural change)  w: section phase

    vec4 uCtl;      // x: master intensity  y: era id (0..7, RAW)
                    // z: song mode (RAW)   w: palette crossfade 0..1

    vec4 uRes;      // x: width px  y: height px  z: aspect  w: render scale

    vec4 uReserved; // schemaVersion 2+ growth. MUST be zero-filled by both hosts.
};
```

**224 bytes = 14 `vec4`.** Count it carefully, because it is easy to get wrong: 10 declarations, of
which `uBands[2]` is two and `uChroma[3]` is three → 13, plus `uReserved` → 14. Mirror in C# as a
`[StructLayout(LayoutKind.Sequential)] struct FeatureFrame` of 14 `System.Numerics.Vector4` fields
and assert `Marshal.SizeOf<FeatureFrame>() == 224` in a unit test. The reserved slot means new
fields can be appended at `schemaVersion 2` without repacking or resizing the buffer — append only,
never reorder.

**Shader dialect bridging.** Write every shader with no `#version` line. Both hosts inject a
preamble at load:

```
// WebGL2 host injects:            // Desktop GL host injects:
#version 300 es                    #version 330 core
precision highp float;             #define highp
precision highp int;               #define mediump
                                   #define lowp
```

With that, shader files are byte-identical across hosts. Verify this in Phase 1 by rendering the
same scripted feature timeline in both and diffing frames.

---

## 6. Architecture and file tree

```
ripple/
├─ harness/                          # Phase 0 — Claude Design output
│  ├─ index.html                     # single-file WebGL2 harness + control panel
│  └─ README.md                      # how to author and export presets
├─ shaders/                          # SHARED. No #version lines. Loaded by both hosts.
│  ├─ common/
│  │  ├─ features.glsl               # the UBO block from §5, verbatim
│  │  ├─ palette.glsl                # LUT sampling, era palettes, chroma→hue
│  │  ├─ sdf.glsl                    # 2D SDF primitives + boolean ops
│  │  ├─ noise.glsl                  # simplex, fBm, curl, domain warp
│  │  └─ safety.glsl                 # luminance rate limiter (§12)
│  ├─ fractal/                       # one program per family; NOT an uber-shader
│  │  ├─ warp_fbm.frag               # DEFAULT/HERO — liquid light show
│  │  ├─ julia_orbit.frag
│  │  ├─ mandel_zoom.frag
│  │  ├─ apollonian.frag
│  │  ├─ kleinian.frag
│  │  └─ mandelbulb.frag             # 3D raymarch, perf-gated
│  ├─ motif/
│  │  ├─ bolt.frag                   # forking lightning, fires on onsets
│  │  ├─ rose.frag                   # phyllotaxis bloom on chord change
│  │  └─ glyph.frag                  # SDF atlas sampler (user-swappable)
│  ├─ passes/
│  │  ├─ warp.frag                   # feedback displacement (MilkDrop core)
│  │  ├─ particles.vert/.frag        # instanced, advected by curl noise
│  │  └─ composite.frag              # tonemap, bloom, grain, vignette, safety
│  └─ fullscreen.vert
├─ presets/
│  ├─ eras/*.json                    # 7 era presets
│  ├─ songs/*.json                   # song-specific overrides
│  └─ generic/*.json                 # non-Dead fallbacks by tempo/energy
├─ data/
│  ├─ songs.json                     # title → song mode map (user-extensible)
│  └─ venues.json                    # optional date → venue lookup for captions
├─ src/Ripple/
│  ├─ Program.cs
│  ├─ Audio/
│  │  ├─ LoopbackCapture.cs          # WASAPI + watchdog + device-change handling
│  │  ├─ RingBuffer.cs               # lock-free single-producer/single-consumer
│  │  ├─ Fft.cs                      # radix-2 real FFT, zero-alloc
│  │  └─ Analyzer.cs                 # the DSP of §7 → FeatureFrame
│  ├─ Meta/
│  │  ├─ SmtcWatcher.cs              # Windows.Media.Control, Spotify-filtered
│  │  ├─ TitleParser.cs              # date/venue/song extraction (§9)
│  │  └─ EraMapper.cs                # date → era id + palette
│  ├─ Render/
│  │  ├─ GlHost.cs                   # Silk.NET window + context + Win32 chrome
│  │  ├─ ShaderLoader.cs             # preamble injection + hot-reload
│  │  ├─ FeedbackChain.cs            # ping-pong FBOs, warp/paint/composite
│  │  ├─ ParticleSystem.cs
│  │  └─ Governor.cs                 # adaptive quality (§11)
│  ├─ Presets/PresetStore.cs         # JSON load + FileSystemWatcher hot-reload
│  └─ Ui/
│     ├─ TrayIcon.cs
│     ├─ Hotkeys.cs                  # RegisterHotKey
│     └─ Hud.cs                      # now-playing caption + diagnostic readout
└─ tests/Ripple.Tests/               # §14
```

### Threading model

Three threads, one queue, no locks in the hot path.

1. **Audio callback thread** (owned by NAudio) — converts the incoming buffer to float32 and writes
   to the ring buffer. Does nothing else. No allocation, no logging.
2. **Analysis thread** — wakes every hop (512 samples ≈ 10.7 ms at 48 kHz), reads a 2048-sample
   window, runs the DSP of §7, and publishes a completed `FeatureFrame` to a single-slot
   double-buffered exchange (`Interlocked.Exchange` on a reference, or two structs plus a volatile
   index).
3. **Render thread** — reads the latest published frame, interpolates toward it, uploads the UBO,
   draws. Never blocks on audio.

SMTC events arrive on the thread pool and mutate a small immutable snapshot object published the
same way.

---

## 7. DSP specification

This is where the reactivity quality is won or lost. Total cost target: **under 1% of one core.** A
2048-point FFT at ~93 Hz is nothing; there is no reason to cut corners here.

**Setup.** Window 2048 samples, hop 512 (≈93 analyses/s at 48 kHz), Hann window, mono sum for
spectral work, L/R retained for stereo features. Real FFT (pack two reals per complex bin, or a
straightforward radix-2 with the imaginary half zeroed — ~80 lines, no external dependency).
Pre-allocate all scratch; use `Span<float>`; no LINQ anywhere in this file.

### 7.1 Perceptual bands (8)

Edges in Hz, derived from the *actual* sample rate:

| Band | Range | Drives, roughly |
|---|---|---|
| sub | 20–60 | global scale, feedback zoom |
| bass | 60–150 | warp amplitude, kick response |
| lowMid | 150–400 | fractal iteration depth |
| mid | 400–1000 | vocal presence, motif opacity |
| hiMid | 1000–2400 | edge detail, line weight |
| presence | 2400–5000 | particle emission |
| brilliance | 5000–10000 | bloom threshold |
| air | 10000–16000 | grain, shimmer |

Per band: `E = Σ|X[k]|² / binCount`, then `dB = 10·log10(E + 1e-12)`.

**Adaptive normalization is mandatory**, and it is the single most important detail in this
document for making the visualizer feel alive on Grateful Dead material. A twenty-minute Dark Star
moves from near-silence to full band peak; fixed scaling gives a dead screen for eight minutes and
a clipped one for two. Per band, track asymmetric envelopes:

```
attack  = 1 - exp(-dt / 0.010)      // 10 ms — hits snap
release = 1 - exp(-dt / 0.250)      // 250 ms — graceful decay
env     = dB > env ? lerp(env, dB, attack) : lerp(env, dB, release)

// normalization window: fast to open up, slow to close down
hi = dB > hi ? dB : hi + (dB - hi) * (1 - exp(-dt / 3.0))
lo = dB < lo ? dB : lo + (dB - lo) * (1 - exp(-dt / 8.0))
band = clamp((env - lo) / max(hi - lo, 6.0), 0, 1)   // 6 dB floor prevents
                                                     // noise-amplification in silence
```

### 7.2 Onset detection

Spectral flux over 40 Hz–8 kHz: `flux = Σ max(0, |X_t[k]| - |X_{t-1}[k]|)`, normalized by
`Σ|X_t[k]| + ε`.

Peak-pick against an adaptive threshold: `thr = median(last 43 flux values) × 1.4 + 0.02`
(43 frames ≈ 460 ms). Refractory period 50 ms. On detection, set `onsetImpulse = 1` and decay it
with a 150 ms time constant.

### 7.3 Tempo tracking and beat phase

Keep 6 s of onset envelope (558 samples at 93 Hz). Autocorrelate over lags 31–93 samples
(180–60 BPM). Disambiguate octave errors by comparing scores at `lag`, `2·lag`, and `lag/2` — pick
the candidate whose score is not dominated by a stronger relative. Median-filter the BPM estimate
over 8 s. Confidence = `peak / mean(acf)`, mapped to 0..1.

Beat phase is a **phase-locked accumulator**, not a per-onset reset — resetting produces visible
jitter:

```
phase += dt * bpm / 60
on onset:  if |phase mod 1| is within 0.25 of a beat boundary:
               nudge phase toward the boundary with gain 0.08
```

**Dead-specific behavior:** when `bpmConfidence < 0.35`, freeze the accumulator and drive
`beatPhase` directly from the decaying onset impulse. This is what makes Drums → Space work
correctly instead of the visuals insisting on a grid that isn't there. Do not skip this.

### 7.4 Chroma, key, and pitch-to-color

For each bin with frequency `f` in 55–5000 Hz: `pc = round(12·log2(f/440)) mod 12`, accumulate
`|X[k]|` with a log-frequency taper. Normalize so max = 1.

Key: correlate the chroma vector against 24 major/minor templates; take argmax for key + mode, with
confidence from the margin over the runner-up.

Map key to hue **around the circle of fifths**, not chromatically:
`keyHue = ((pitchClass · 7) mod 12) / 12`. Musically adjacent keys then land on adjacent hues, so a
modulation reads as a *related* color shift rather than a jump — which is the whole point of
synesthetic mapping. A jam in E and one in A look like cousins.

`chromaFlux` = L1 distance between the current chroma vector and its 500 ms EMA. This is the chord
change detector that fires the rose bloom.

### 7.5 Stereo, percussivity, structure

- **Stereo width** via mid-side: `M=(L+R)/2`, `S=(L-R)/2`,
  `width = rms(S) / (rms(M) + rms(S) + ε)`. Cheaper and far more stable than L/R correlation. This
  is a genuinely useful Dead feature — the Wall of Sound era images very differently from a 1969
  two-track, and driving symmetry-breaking from it gives free era differentiation.
- **Percussivity** via HPSS-lite: median-filter the magnitude spectrogram across time (window 5)
  for a harmonic estimate `H`, across frequency (window 17) for a percussive estimate `P`;
  `perc = ΣP / (ΣP + ΣH)`. Drums drive motif and particle layers; sustain drives the fractal flow.
- **Novelty** = cosine distance between the current 8-band vector and its 8 s EMA. Spikes at
  section boundaries — use it to trigger preset crossfades and fractal-family changes.

---

## 8. Visual system

### 8.1 Architecture: copy MilkDrop's, not a modern deferred renderer

MilkDrop ran convincingly on 2001 hardware because its core is *not* an expensive per-pixel
evaluation — it is **a cheap warp of the previous frame plus a small amount of new paint**. That
architecture is the reason R3 is achievable. Adopt it directly:

```
[ping/pong FBO, RGBA16F, at renderScale (default 0.75×)]

Pass A — WARP:    sample previous frame at displaced UV
                  (rotation + zoom + fBm domain warp, modulated by
                   sub/bass and stereo width), multiply by decay 0.94–0.99
Pass B — PAINT:   additively composite the fractal layer, motif SDFs,
                  and particles into the current buffer
Pass C — COMPOSITE: tonemap → bloom (2-tap down/up, not a full pyramid)
                  → grain → vignette → safety clamp → backbuffer at full res
```

Feedback decay is the highest-value-per-cost parameter in the entire system. Long decay gives the
trailing, liquid, self-similar quality that reads as *psychedelic* rather than as *audio meter*.
Expose it per preset and give it a small onset-driven modulation.

### 8.2 Fractal families

Separate programs, compiled at startup, selected per preset. Do **not** build an uber-shader with a
mode branch — register pressure from the union of all families will slow down the cheap ones.

| Program | Character | Cost | Notes |
|---|---|---|---|
| `warp_fbm` | **Hero / default.** Recursive domain-warped fBm. Reads as oil-and-water liquid light projection — the actual Fillmore/Avalon aesthetic. Warp amount ← bass, warp rotation ← stereo width. | Low | Never repeats. Start here; ship this even if nothing else lands. |
| `julia_orbit` | Julia set with `c` orbiting a closed path; path radius ← dynamics, position ← beat phase. Morphing `c` on the beat is the classic and still the best. | Low | 64 iterations is plenty at 0.75× scale. |
| `mandel_zoom` | Slow autonomous zoom, escape-time coloring ← chroma. | Low–med | **Bound the zoom depth and period-reset** to stay inside float precision; do not attempt deep-zoom perturbation. |
| `apollonian` | Gasket via inversive folding. Psychedelic lace, strongly geometric. | Low | Excellent contrast against `warp_fbm`. |
| `kleinian` | Kleinian limit sets via Möbius folding. Genuinely strange. | Med | Reserve for Space. |
| `ifs_flame` | Flame-style IFS accumulated **via the particle system** into the feedback buffer, not evaluated per pixel. A nod to Electric Sheep's generative lineage, at a fraction of the cost. | Low | Clever and cheap — the accumulation buffer already exists. |
| `mandelbulb` | 3D raymarched. | **High** | Perf-gated: half-resolution, reduced step count, and only enabled when the governor reports sustained headroom. Never the default. |

### 8.3 Motif layer

SDF-based and procedural, composited in Pass B. This is where the Dead idiom enters.

- **Lightning bolts** — procedurally forking SDF polylines, fired on strong onsets when
  `percussivity` is high, with a fast bloom-heavy decay.
- **Rose bloom** — phyllotaxis spiral petals that open on `chromaFlux` spikes. Chord changes bloom
  roses. This is the single most legible music→image mapping in the whole design and worth polishing.
- **Tie-dye spiral warp** — a rotating spiral applied to the *palette lookup* rather than to
  geometry, spun at BPM/4. Cheap, and it does more for period authenticity than any amount of
  geometry.
- **Glyph atlas** — a small SDF texture atlas of silhouettes (turtles, skeletal forms, bears) that
  emerge from and dissolve back into the fractal on section boundaries. Instanced, low count, used
  as accent, never wallpaper.

**On iconography and IP:** the Dead's specific marks — the Steal Your Face skull-and-bolt, the
dancing bears, Rick Griffin's skeleton-and-roses — are protected by Grateful Dead Productions.
For a personal desk toy nothing here is a practical concern, but the design should generate
**original geometry in the shared visual language** (bolts, roses, skulls, terrapins, liquid light,
Fillmore poster palettes), which are genre idioms rather than protected marks. Ship
`motif/glyph.frag` reading a user-supplied SDF atlas from `data/glyphs/`, empty by default, so
dropping in personal assets is a one-file operation and stays the user's own decision.

### 8.4 Particles

GPU-instanced points, budget 4k–64k under governor control, additive blend, advected by curl noise
derived from the fractal's own gradient field. Emitted on onsets, with emission rate ← `presence`
band. Cheap, and adds more perceived life per GPU-cycle than any other layer.

### 8.5 The now-playing caption — the actual talking-point trigger

Swirling fractals are a screensaver. What makes a passerby stop is realizing the machine
*understands* the music. Two small pieces of chrome do almost all of that work:

1. **Track caption.** Fades in for 8 s on track change, bottom-left, small: song title, artist, and
   when a date is parsed, the show date and venue — `Dark Star · Veneta, OR · 8/27/72`. A person who
   knows the Dead will stop walking.
2. **Diagnostic readout** (off by default, `Ctrl+Alt+D`). Detected BPM, key, era, active fractal
   family, and current frame time. This is what converts "nice screensaver" into "wait, it's
   analyzing it in real time?" — and it doubles as the debugging HUD during development.

Typography: pick something period-appropriate but legible at small sizes. Do not use a
psychedelic display face for the caption — it will be unreadable from three feet away, which
defeats the entire purpose.

---

## 9. Dead intelligence layer

### 9.1 Metadata source

SMTC session filtered to Spotify via `SourceAppUserModelId`
(`Spotify.exe` / `SpotifyAB.SpotifyMusic_...` for the Store build — match on substring, handle
both). Subscribe to `MediaPropertiesChanged` and `PlaybackInfoChanged`; also poll every 2 s as a
safety net, because SMTC events are occasionally missed.

Available fields: `Title`, `Artist`, `AlbumTitle`, `AlbumArtist`, `TrackNumber`, `Thumbnail`,
`PlaybackStatus`, and `TimelineProperties` for position. Optionally sample the album-art thumbnail
down to a 5-color palette and blend it into the era palette at low weight — near-free and it ties
the visuals to the release.

### 9.2 Parsing

**Artist gate** — extended family all qualifies for era treatment: Grateful Dead, Jerry Garcia,
Jerry Garcia Band, JGB, Legion of Mary, Bob Weir, Ratdog, Phil Lesh (and Friends), Furthur,
Dead & Company, Billy & the Kids, New Riders of the Purple Sage, Joe Russo's Almost Dead, Dark Star
Orchestra. No match → generic mode (§9.4).

**Date extraction**, tried in order against `AlbumTitle` then `Title`:

```
(19[6-9]\d)[-/.](\d{1,2})[-/.](\d{1,2})       ISO-ish → 1977-05-08
\b(\d{1,2})[-/.](\d{1,2})[-/.]((?:19)?\d{2})\b  M/D/YY → 5/8/77
'(\d{2})\b                                     year only → Europe '72
\b(19[6-9]\d)\b                                bare year
```

Plus a keyword table for the release series that don't carry dates in a parseable form: Dick's
Picks, Dave's Picks, Road Trips, Download Series, One/Two From The Vault, Ladies and Gentlemen,
Cornell, Winterland, Fillmore, Europe '72. Map these to a `data/venues.json` lookup for the
caption. Keep the table in data, not code.

**Title normalization** before song matching: strip `- Live at …`, `[Live]`, `(Remastered …)`,
`(2013 Remaster)`, `Pt. 1` / `Pt. 2`, and trailing dates. Handle segues — a title containing `>` or
`->` splits into multiple songs; match the first, set a `segue` flag, and crossfade presets at the
midpoint of the track. Small touch, disproportionate delight.

### 9.3 Era mapping

Seven eras, each a palette plus a motion character. Palettes below are starting points grounded in
period referents (poster inks, liquid-light oil wheels, room tone) — tune them in the harness, and
treat the *motion* column as equally important. A palette swap alone will not read as an era change
from across a room; the way the image moves has to change too.

| id | Era | Palette direction | Motion character |
|---|---|---|---|
| 0 | 1965–67 Primal | High-contrast Day-Glo: `#FF2D95` `#FFE800` `#00E5FF` `#1A0033` | Fast churn, short feedback decay, raw and unstable |
| 1 | 1968–70 Live/Dead | Earthy ochre and rust: `#C9762B` `#8C2F1E` `#E8D5A3` `#2B1A14` | Slower warp, organic fBm, low geometry |
| 2 | 1971–74 Europe '72 / Wall of Sound | Cool crystalline: `#4A7FC1` `#8B6FD4` `#D9E4F5` `#0E1729` | Wide stereo-driven symmetry, precise Julia geometry, long decay |
| 3 | 1976–79 Terrapin | Warm amber and teal: `#E8A33D` `#2E8B84` `#F2E3C4` `#1F2419` | Tighter BPM lock, rose and terrapin motifs foreground |
| 4 | 1980–86 Brent | Neon on black: `#00FF9C` `#FF3D6E` `#FFD400` `#050508` | Sharper edges, higher contrast, faster motif cycling |
| 5 | 1987–90 In the Dark | Saturated jewel: `#7B2FBE` `#00B8D4` `#FF6B35` `#12061F` | Denser particles, heavier bloom |
| 6 | 1991–95 Final | Deep violet, smoky: `#3D2B56` `#6B4C93` `#C9A8E0` `#0A0710` | Slowest warp, longest decay, most feedback |
| 7 | Unknown / studio | Neutral, chroma-driven | Derived entirely from audio features |

### 9.4 Song modes

A JSON map from normalized title → mode. Ship ~15 and let the user extend the file; hot-reload it.
The point is not exhaustive coverage — it is that the handful of songs a Dead listener would *test
it with* each do something specific and correct.

```json
{
  "dark star":            { "fractal": "mandel_zoom",  "feedbackDecay": 0.995, "tempoLock": false },
  "terrapin station":     { "fractal": "warp_fbm",     "motifs": ["rose", "glyph:terrapin"] },
  "fire on the mountain": { "fractal": "warp_fbm",     "palette": "ember", "particles": "rising" },
  "drums":                { "fractal": "ifs_flame",    "tempoLock": true,  "percussiveGain": 2.0 },
  "space":                { "fractal": "kleinian",     "tempoLock": false, "feedbackDecay": 0.998 },
  "st stephen":           { "fractal": "julia_orbit",  "motifs": ["rose"], "bloomOnChordChange": 1.5 },
  "china cat sunflower":  { "fractal": "apollonian",   "palette": "dayglo" },
  "eyes of the world":    { "fractal": "warp_fbm",     "warpRotation": 0.4, "palette": "era" },
  "ripple":               { "fractal": "warp_fbm",     "feedbackDecay": 0.99, "intensityScale": 0.7 }
}
```

**Graceful degradation is mandatory.** Spotify's Dead catalog metadata is uneven — official live
releases carry dates, studio albums and some compilations do not. The chain must be:
song mode → era → audio-features-only generic mode, with each step working alone. A visualizer that
only looks good on well-tagged tracks fails in the demo. Test it on a studio album and on non-Dead
music, and make both look intentional.

---

## 10. Preset schema

One schema, loaded by both hosts, hot-reloaded from disk.

```json
{
  "schemaVersion": 1,
  "id": "era-europe72",
  "name": "Europe '72",
  "fractal": "julia_orbit",
  "palette": { "stops": ["#0E1729","#4A7FC1","#8B6FD4","#D9E4F5"], "chromaBlend": 0.35 },
  "feedback": { "decay": 0.985, "zoom": 1.004, "rotate": 0.0015, "warpAmount": 0.35,
                "warpScale": 2.4, "bassGain": 1.2 },
  "fractalParams": { "iterations": 96, "escapeRadius": 4.0, "orbitRadius": 0.28,
                     "beatMorph": 0.6 },
  "motifs": { "bolt": { "gain": 0.8, "threshold": 0.55 },
              "rose": { "gain": 1.0, "chromaFluxThreshold": 0.4 },
              "glyph": { "atlas": "default", "gain": 0.2 } },
  "particles": { "count": 24000, "curlScale": 1.8, "emitBand": 5, "lifetime": 3.2 },
  "post": { "bloomThreshold": 0.7, "bloomGain": 0.5, "grain": 0.03, "vignette": 0.25 },
  "mapping": { "sub": "feedbackZoom", "bass": "warpAmount", "stereoWidth": "symmetry",
               "keyHue": "paletteRotate", "novelty": "fractalMorph" },
  "safety": { "maxLuminanceDelta": 0.08, "maxBrightness": 0.85 }
}
```

The `mapping` block is what makes the harness a real authoring tool: feature→parameter routing is
data, not code, so new looks come from editing JSON rather than writing shaders.

---

## 11. Performance governor and budget enforcement

R3 is a hard requirement, so it gets machinery.

**Quality dials, in the order the governor should pull them:**

1. `renderScale` — 1.0 → 0.75 → 0.6 → 0.5 (largest, cheapest win)
2. `particles.count` — 64k → 24k → 8k → 4k
3. `fractalParams.iterations` — 128 → 96 → 64 → 32
4. Bloom taps — 3 → 2 → 1 → off
5. Disable `mandelbulb`, fall back to `warp_fbm`

**Control loop.** EMA of frame time with hysteresis to prevent oscillation:

```
budget = 1000 / targetFps * 0.7          // 70% of the frame, leave headroom
if frameTimeEma > budget for 30 consecutive frames  → step down one dial
if frameTimeEma < budget * 0.55 for 300 frames      → step up one dial
```

Prefer GL timer queries (`GL_TIME_ELAPSED`) for true GPU time; fall back to CPU-side wall clock if
unavailable.

**Idle states — this is what makes it acceptable to leave running all day.**

| Condition | Behavior |
|---|---|
| SMTC reports `Paused` or `Stopped` | Enter idle immediately |
| RMS below −50 dBFS for 3 s | Enter idle: 10 fps, freeze feedback decay, dim to 30% |
| Window minimized or fully occluded | **Stop rendering entirely.** Block on the event loop; do not spin at 1 fps |
| Audio resumes | Ramp back to full over 500 ms |

**Optional power tip:** on a laptop or a machine with an iGPU, running this on the integrated GPU
keeps the discrete GPU entirely free for real work. Adapter selection under OpenGL is awkward —
export `NvOptimusEnablement = 0` / `AmdPowerXpressRequestHighPerformance = 0`, or just set the
per-app graphics preference to "Power saving" in Windows Settings. Document the Settings route; it
is more reliable than the export trick.

**Hard budget test (Phase 4 gate).** Automated, not manual: run 60 s driven by a scripted synthetic
feature timeline, assert `p99 frameTime < 16.6 ms`, `workingSet < 60 MB`, `Gen0 collections == 0`
during the measured window. Zero Gen0 collections is the check that actually catches accidental
per-frame allocation — it will fail loudly the first time someone writes a LINQ expression in the
render loop, which is exactly what it is for.

---

## 12. Safety limiter

This runs where anyone can walk past it, including people with photosensitive epilepsy, which makes
this a real design requirement rather than a nicety. It is also cheap — a handful of lines in the
composite pass.

- **Luminance rate limit.** Track mean frame luminance; clamp frame-to-frame delta to
  `maxLuminanceDelta` (default 0.08). Implement by computing mean luminance in the composite pass
  (or from a 1×1 mip of the previous frame) and scaling output.
- **No full-field flashes above 3 Hz.** Rate-limit any global brightness modulation.
- **Brightness ceiling** at `maxBrightness` (default 0.85) — never full white.
- **Avoid saturated red↔blue alternation**, which is the highest-risk pattern. Constrain palette
  interpolation paths to avoid crossing between saturated red and saturated blue at high frequency.
- **Master intensity** slider, and a global `Ctrl+Alt+[` / `]`.

Implement in `shaders/common/safety.glsl` and apply unconditionally in `composite.frag`. Not
preset-overridable below a floor.

---

## 13. Window, controls, config

- **Window:** borderless, remembered geometry, default 960×540 in a screen corner. Optional
  always-on-top. Fullscreen-on-a-chosen-monitor toggle — this is the "someone actually stopped"
  mode. Silk.NET uses GLFW; borderless / topmost / click-through need Win32 interop
  (`SetWindowPos`, `SetWindowLong` with `WS_EX_TOPMOST` / `WS_EX_TRANSPARENT` / `WS_EX_LAYERED`).
- **Tray icon:** show/hide, preset picker, lock preset, pause, quit.
- **Global hotkeys** (`RegisterHotKey`): `Ctrl+Alt+V` show/hide · `Ctrl+Alt+F` fullscreen ·
  `Ctrl+Alt+N` next preset · `Ctrl+Alt+L` lock preset · `Ctrl+Alt+D` diagnostic HUD ·
  `Ctrl+Alt+S` screenshot to `%USERPROFILE%\Pictures\Ripple` · `Ctrl+Alt+[` / `]` intensity.
- **Config** at `%APPDATA%\Ripple\config.json`. Presets, `songs.json`, and shaders all hot-reload
  via `FileSystemWatcher`. Shader hot-reload in particular is worth the hour it costs — it turns
  post-handoff tuning from a rebuild cycle into a save-and-look cycle, which means the tool keeps
  improving after Claude Code is done with it.

---

## 14. Testing

**DSP unit tests** — the part most likely to be subtly wrong while looking fine:

- FFT against known signals (impulse, DC, single-bin sine) with a magnitude tolerance.
- Band mapping: synthetic sine at each band center lights that band and no neighbor above 0.1.
- Tempo tracker against synthetic click tracks at 60–180 BPM, including a **linearly drifting**
  track (±8% over 60 s) — the Dead case. Assert convergence within 4 s and tracking within 2 BPM.
- Chroma against synthetic sine sets forming known triads; assert correct key detection.
- `TitleParser` against a fixture list of ~40 real Spotify Dead album/track strings, including
  studio albums with no date, segued titles, and `(Remastered)` noise. Include the failures — assert
  they degrade to `era: unknown` rather than producing a wrong date.
- `FeatureFrame` size assertion (224 bytes) and a std140 round-trip test: upload a known frame,
  read it back in a compute or via a debug shader writing to a 1×1 target, compare.

**Visual regression** — script a fixed feature timeline with a fixed RNG seed, render N frames,
compare to golden images by SSIM with a tolerance. Catches shader refactors that silently change
the look. The Phase 0 harness can be gated the same way with Playwright screenshots.

**Cross-host parity** (Phase 1 gate) — render the same scripted timeline in the harness and the
native app; diff frames. This is the test that proves the shared-shader contract actually holds.

---

## 15. Phase plan with acceptance gates

Each gate is a thing that either demonstrably works or does not. Do not proceed past a failed gate.

| Phase | Where | Work | Gate |
|---|---|---|---|
| **0** | Claude Design | WebGL2 harness (see `CONTRACTS.md` §3 for the shader-extraction convention): full pipeline (feedback/warp/fractal/motif/particles/post), synthetic feature driver with per-field sliders, mic and file input, preset editor with JSON export. Implement `warp_fbm` + `julia_orbit` + rose/bolt motifs first. | The look is decided. At least 4 presets exported that are genuinely good on real audio via mic. Shaders extracted to `shaders/` via `tools/extract-shaders.mjs`. |
| **1** | Claude Code | Native skeleton: Silk.NET window + GL 4.3 context, shader loader with preamble injection, feedback chain, UBO upload, **synthetic feature driver ported from the harness**. No audio yet. | Native app renders the Phase 0 presets. Cross-host frame diff passes. This gate proves the shared-shader bet paid off. |
| **2** | Claude Code | WASAPI loopback + ring buffer + full DSP of §7. Device-change watchdog. Diagnostic HUD. | Real Spotify audio drives the visuals. DSP unit tests green. Tempo tracker holds on a drifting click track and on an actual Dead jam. |
| **3** | Claude Code | SMTC watcher, title/date parsing, era mapper, song modes, preset crossfade on track change, now-playing caption. | Play three tracks — a dated live release, a studio album, and a non-Dead track — and get three visibly different, intentional-looking results. |
| **4** | Claude Code | Governor, idle states, safety limiter, budget test. | **All of R3's numbers met by automated test.** Sustained 8 h run with no leak (working set flat within 5 MB). |
| **5** | Claude Code | Tray, hotkeys, config, fullscreen mode, preset library expansion (all 7 eras + ~15 song modes), packaging. | Single-file publish runs on a clean Windows 11 machine with no SDK. |
| **6** | Optional | Per-process audio isolation (§16), extra fractal families, video export, album-art palette extraction. | — |

Phases 0 and 1 are the risky ones. If cross-host parity fails at gate 1, stop and fix the contract
rather than forking the shaders — a fork here compounds through every later phase.

---

## 16. Risks and fallbacks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Cross-host shader parity fails (precision, UBO packing, `mediump` behavior) | Medium | The vec4-only UBO layout removes the biggest cause. If parity still fails, treat the desktop host as authoritative and add a `#define RIPPLE_WEBGL` for the small number of divergences — do not fork files. |
| WASAPI loopback stops on device change | High (will happen) | Watchdog + `MMDeviceEnumerator` notifications, built in Phase 2, not bolted on later. |
| Tempo tracking flails on free-time passages | High on Dead material | The `bpmConfidence < 0.35` fallback in §7.3 is designed for exactly this. Test explicitly against a Drums→Space passage. |
| Metadata too sparse for era detection | Medium | Three-tier degradation (song → era → audio-only), each independently good. |
| Governor oscillates between quality levels | Medium | Asymmetric hysteresis (30 frames down, 300 frames up) plus a 5 s minimum dwell at each level. |
| CsWinRT / SMTC access throws in some contexts | Low | Wrap all SMTC access behind `IMetadataSource` with a null implementation; the app must run fully (audio-only mode) if metadata is unavailable. Never let metadata failure take down rendering. |
| Footprint creeps past 60 MB | Medium | The Phase 4 budget test is a gate, not a report. |

**Phase 6 — per-process audio isolation.** Windows 10 2004+ supports process-specific loopback via
`ActivateAudioInterfaceAsync` with `AUDIOCLIENT_ACTIVATION_PARAMS`, `PROCESS_LOOPBACK`, and
`INCLUDE_PROCESS_TREE` targeting the Spotify PID — which would exclude Teams dings and browser
audio. This requires raw COM interop from C#; verify whether NAudio wraps it before hand-rolling.
Worth doing eventually, not worth blocking v1.

---

## 17. Handoff prompts

### To Claude Design (Phase 0)

> Build a single-file WebGL2 music visualizer harness, `harness/index.html`, per §4, §5, §8, and
> §10 of `ripple-viz-build-plan.md`.
>
> Two panes: a full-bleed canvas running the complete render pipeline (ping-pong feedback buffer →
> warp pass → fractal paint → motif SDFs → particles → composite with bloom/grain/vignette/safety
> clamp), and a collapsible control panel.
>
> Implement `warp_fbm` (the hero — recursive domain-warped fBm reading as an oil-and-water liquid
> light projection) and `julia_orbit` first, plus the rose and lightning-bolt motifs. Write all
> shaders **with no `#version` line**, injecting the WebGL2 preamble at load, and implement the
> Feature Frame UBO exactly as specified in §5 — vec4-packed, 224 bytes, same member names. This
> exact layout will be consumed by a native OpenGL host later, so treat it as a frozen contract.
>
> Three input modes: (a) a **synthetic feature driver** with a slider and optional LFO for every
> Feature Frame field — this is the primary mode and the point of the harness; (b) microphone; (c) an
> uploaded audio file. Include a preset editor that reads and writes the §10 JSON schema, with
> download.
>
> For the control panel chrome, read `/mnt/skills/public/frontend-design/SKILL.md` first. It should
> read as lab instrumentation — dense, monospaced, legible in a dark room, no decoration. Spend the
> entire aesthetic budget on the canvas; the panel is a tool.
>
> Deliver at least four exported presets that look genuinely good on real music through the mic.

### To Claude Code (Phases 1–5)

> Build the native application described in `ripple-viz-build-plan.md`. Read the whole document
> first, particularly §2 (verified constraints — do not reintroduce the Spotify audio-analysis API
> or the Web API), §5 (the Feature Frame contract), §7 (DSP), and §11 (performance budget).
>
> Stack: .NET 10 LTS, TFM `net10.0-windows10.0.19041.0`, Silk.NET (Windowing + OpenGL + Input), NAudio,
> `Windows.Media.Control`. Do not use NativeAOT. Pin whatever package versions `dotnet add package`
> resolves and record them in the README.
>
> Work phase by phase against the gates in §15 and stop at any gate that fails. Phase 1's gate —
> cross-host frame parity with the Phase 0 harness using identical shader files — is the one that
> validates the whole architecture; if it fails, fix the contract rather than forking the shaders.
>
> Consume `harness/`, `shaders/`, and `presets/` from Phase 0 as-is. The shader files are shared
> source, not a starting point to rewrite.
>
> Non-negotiables: zero heap allocation in the render loop (§11's zero-Gen0 assertion is a gate);
> the safety limiter of §12 applies unconditionally and is not preset-overridable below its floor;
> the three-tier metadata degradation of §9.4 must leave the app fully functional with no metadata
> at all.

---

## 18. Appendix: why this ends up lightweight

Worth stating plainly, because "lightweight fractal visualizer" sounds contradictory:

1. **The expensive thing is not done per pixel per frame.** MilkDrop's architecture — warp the
   previous frame, add a little paint — means the image accumulates complexity over time instead of
   being computed from scratch. A long feedback decay does more visual work than a high iteration
   count, and costs a texture sample.
2. **The fractal pass runs at 0.75× and upscales.** At a 960×540 window that is roughly a 720×405
   fractal evaluation — trivial for any GPU made in the last decade.
3. **The DSP is genuinely cheap.** A 2048-point FFT at 93 Hz, plus some median filters, is well
   under 1% of one core. There is no reason to compromise analysis quality to save CPU here, which
   is why §7 specifies good algorithms rather than fast approximations.
4. **It does nothing when nothing is happening.** Paused Spotify, silence, or a hidden window all
   drop to near-zero cost. Most of an 8-hour workday, this program is asleep.
