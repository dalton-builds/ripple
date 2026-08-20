# Ripple

A lightweight Windows music visualizer that reacts to Spotify desktop audio, blending fractal
geometry with liquid-light-show and Grateful Dead visual idiom.

Full spec: **`ripple-viz-build-plan.md`** (read it before writing code).

---

## Toolchain

**Build and run through the wrappers, not through a bare `dotnet`:**

```powershell
.\build.ps1                    # debug build
.\build.ps1 -Configuration Release
.\build.ps1 -Test              # solution-wide, runs Ripple.Tests
.\build.ps1 -Publish           # single-file self-contained win-x64
.\run.ps1 --preset era-europe72 --size 960x540
```

`global.json` pins SDK **10.0.400** with `rollForward: latestFeature`, so any 10.0.4xx
works and a 9.x refuses rather than silently building against the wrong framework.

The wrappers exist because **`dotnet` on PATH is not necessarily an SDK install.** On this
project's primary machine `C:\Program Files\dotnet` ships the 8.0 and 9.0 *runtimes* and no
SDK at all, while the pinned SDK sits under the user profile — so a bare `dotnet build` fails
with "A compatible .NET SDK was not found" even though the right SDK is present.
`scripts\dotnet-resolve.ps1` resolves the muxer explicitly, in order:

1. `$env:DOTNET_ROOT`
2. `$HOME\.dotnet\dotnet.exe` — where a user-scoped install lands
3. `C:\Program Files\dotnet\dotnet.exe`
4. whatever `dotnet` is on PATH

and takes the first that actually reports an SDK. If none do, it says so and points at
`winget install Microsoft.DotNet.SDK.10`.

GPU needs **OpenGL 4.3**. The shader preamble is `#version 430 core`, not 330: the desktop
branch of `features.glsl` declares the UBO as `layout(std140, binding = 0)`, and the
`binding` qualifier on a uniform block is GLSL 4.20+. Under 330 the driver reports
`'binding' : syntax error`, which reads like a malformed file rather than a version floor.

---

## Start here

### Step 1 — Verify the environment (5 minutes)

```powershell
dotnet --list-sdks        # need a 10.x SDK
git --version
```

If no 10.x SDK: install the **.NET 10 SDK** from https://dotnet.microsoft.com/download
(.NET 8 and 9 both reach end of support on 2026-11-10 — do not target them.)

GPU needs OpenGL 4.3, which is anything made in roughly the last decade. No Visual Studio
required; the `dotnet` CLI is sufficient. Silk.NET ships its native binaries via NuGet.

### Step 2 — Initialize the repo

```powershell
cd C:\dev\ripple          # or wherever
git init
git add -A
git commit -m "Scaffold: frozen contracts, era presets, song modes"
```

### Step 3 — Kick off Phase 0 in Claude Design (do this first, it's the long lead)

Phase 0 decides the *look*, needs no local setup, and produces the shaders and presets that
Phase 1 consumes. Start it now and do the rest in parallel. Use the handoff prompt in §17 of the
build plan, plus the shader-extraction requirement in `CONTRACTS.md`.

### Step 4 — Point Claude Code at this repo

```powershell
claude
```

Then: *"Read ripple-viz-build-plan.md and CONTRACTS.md, then start Phase 1."*

Phase 1 builds the native shell against a synthetic feature driver — **no audio and no Phase 0
output required**. It can proceed in parallel with the harness work and gets you a running
window today.

---

## What is already frozen

These four things exist so that Claude Design and Claude Code build against identical
definitions. Drift between them is the main architectural risk in this project. See
`CONTRACTS.md`.

| File | What it fixes |
|---|---|
| `shaders/common/features.glsl` | The Feature Frame UBO. 14 vec4 = 224 bytes (incl. 1 reserved). Shared verbatim by both hosts. |
| `presets/eras/*.json` | 8 era presets (7 Dead eras + audio-only fallback), palettes and motion character. |
| `data/songs.json` | 19 song-mode overrides, merged over the era preset. |
| `data/venues.json` | Date and release-series lookups for the now-playing caption. |

All four hot-reload from disk at runtime. Tune them without rebuilding.

---

## Phase status

- [x] **0** — WebGL2 harness, 5 presets authored, shaders extracted, timeline recorder working
- [x] **1** — Native shell renders all 13 presets; cross-host parity gate passes (mean SSIM
  1.0000, min 0.9998 over 780 frames x 13 presets). See "Cross-host parity gate" below.
- [x] **2** — WASAPI loopback + full DSP; real audio drives the visuals, 60 DSP tests
  green, and a captured real-audio timeline replays in the harness. See "Audio and DSP".
- [ ] **3** — SMTC metadata, era mapping, song modes, now-playing caption
- [ ] **4** — Governor, idle states, safety limiter, budget test green
- [ ] **5** — Tray, hotkeys, config, packaging, full preset library

Do not proceed past a failed gate. Gate 1 (cross-host parity) validates the entire
shared-shader architecture; if it fails, fix the contract rather than forking the shaders.

---

## Resolved package versions

Resolved by the first `dotnet restore`; the csproj uses `*` deliberately so the first
resolve picks current versions rather than stale ones from a spec.

| Package | Resolved version |
|---|---|
| Silk.NET.Windowing | 2.23.0 |
| Silk.NET.OpenGL | 2.23.0 |
| Silk.NET.Input | 2.23.0 |
| NAudio | 3.0.1 |

NAudio is restored but unused until Phase 2.

---

## Sanctioned shader divergences

Per `CONTRACTS.md` §2, divergences are guarded, never forked. Current count: **1**.

| Location | Guard | Why |
|---|---|---|
| `shaders/common/features.glsl` | `#ifdef RIPPLE_WEBGL` | GLSL ES 3.00 rejects `layout(binding=N)` on a uniform block (it lands in ES 3.10 / GL 4.2). The harness declares the block without it and calls `glUniformBlockBinding(prog, idx, 0)` at link time — bit-identical in effect. |

Keep this table current. A growing count means the contract is drifting.

**Phase 1 added none.** The parity gate passed without one. Every residual cross-host
difference traced to a host-side or fixed-function cause, so there was nothing an `#ifdef`
could have fixed and nothing that justified forking a file.

## Cross-host parity gate

Gate 1 validates the shared-shader architecture: render the same scripted Feature Frame timeline
in both hosts and diff the frames. Both replay in exact step mode — timeline row *i* renders as
frame *i*, `dt` pinned to `1/rate`, no wall clock anywhere in the path.

```powershell
.\build.ps1                                  # the gate does not build for you
node tools/parity-gate.mjs --mode a           # dump both hosts, compare, exit nonzero on fail
node tools/parity-gate.mjs --mode a --all-timelines
node tools/parity-gate.mjs --mode b           # informational curve
```

**Everything the gate depends on lives in `tests/parity-config.json`** — the exclusions, the
dump geometry, the thresholds, the timelines and the recorded baselines. That is deliberate:
when Phase 4 touches the composite pass and the gate is re-run, it has to re-run with the
*same* exclusions that established the baseline, and a config file remembers those where a
command line does not. Both hosts read it (`--parity-mode a`, via
`src/Ripple/ParityProfile.cs` and `tools/parity-config.mjs`), and each dump's
`manifest.json` records which terms it actually rendered, so `compare-ssim.mjs` refuses to
quietly grade two dumps that were never comparable.

The individual `--no-feedback` / `--no-grain` / `--no-particles` flags remain for
diagnosis, and anything passed explicitly on the command line overrides the config.

`parity-harness.mjs` drives `harness/index.html` in headless Chrome over the DevTools
Protocol, calling the `window.__ripple` automation surface at the bottom of that file. Every
entry there is a thin wrapper over what the panel buttons call, so the automated path and the
hand-driven path are the same code. Dependency-free, like the other tools here.

### Why there are two modes

**Ripple is a feedback renderer**, and its per-frame paint is deliberately a sparse
high-contrast dither spray — the image exists only as the *integral* of many frames. Three
terms therefore cannot agree between two GL implementations, by construction rather than by
defect:

| Term | Why it cannot match | Excluded by |
|---|---|---|
| Feedback carry | Each frame warps the previous one, so any difference is fed back and amplified | `--no-feedback` |
| Film grain | `hash12()` of `vUv`, a `fract` chain chaotic in the last bit of its input — an FMA contracted one way by the D3D compiler and another by the GL driver decorrelates it completely | implied by `--no-feedback` |
| Particles | `GL_POINTS`; ANGLE expands sized points to quads on D3D11 where desktop GL rasterizes them natively, so sub-pixel coverage differs | `--no-particles` |

**Mode A** (`--mode a`, the real gate) excludes all three, so each frame is computed fresh
from its Feature Frame and the diff measures shader math. Threshold: per-frame SSIM >= 0.995.

**Mode B** (`--mode b`, informational) is full feedback over the whole 20 s. Its only
assertion is a smoke check at frame 60; the point is the shape of the curve, because a cliff
means a real bug hiding behind expected divergence.

Do **not** extend the exclusion list or lower the threshold to make a run pass. Each exclusion
above is justified by a measurement, and each was confirmed by looking at the amplified diff
image the tool writes for any frame under threshold.

### Results — 2026-08-20, 13 presets x 60 sampled frames of a 1200-frame timeline

Harness: ANGLE / Direct3D11 on Intel UHD, RGBA16F. Native: OpenGL 4.3 on the same GPU.

| Mode | Mean SSIM | Min | Verdict |
|---|---|---|---|
| A — strict | **1.0000** | 0.9998 | **pass**, all 13 presets |
| A — particles included | 0.9948 | 0.9425 | fails; every failing frame's diff is pure particle speckle |
| B — full feedback, 20 s | 0.9924 | 0.8930 | 12/13 clear the frame-60 check |

Mode B's curve is **flat, not decaying**: it wanders in a 0.97–0.999 band for 20 s rather than
compounding, so the feedback loop is contractive — `decay` damps a perturbation faster than
the warp amplifies it. The largest single step between sampled frames is under 0.013 for eleven
presets; `era-brent` (0.075) and `scarlet-lace` (0.030) are the exceptions, and both are the
most particle-dominated presets in the library. `era-brent` is also the one preset that misses
the frame-60 check, at 0.9155 — same cause, amplified by its very high-contrast palette against
a near-black background.

### Diagnosing a failure

Reach for `compare-trace.mjs` **before** the diff images. Both hosts dump a per-frame trace of
all 58 draw-state scalars with `--trace`, and the tool names the parameter that diverged and
the frame it first moved:

```powershell
node tools/parity-harness.mjs --timeline tests/fixtures/timeline-20s.json `
     --out captures/tr-harness --preset beam-julia --stride 600 --trace
.\run.ps1 -NoBuild --dump --trace --timeline tests/fixtures/timeline-20s.json `
     --out captures/tr-native --preset beam-julia --stride 600
node tools/compare-trace.mjs captures/tr-harness captures/tr-native
```

That is what found the one real bug this gate caught. `tlStepBegin()` reset `palRotSmooth`,
`palRotPrev`, `hueRate` and `spiralSpin` but not `D.palRot` — which `resetDraw()`
deliberately never clears, because it accumulates across frames. So a harness parity run
inherited however much palette rotation the page had piled up since load, and in a
`DUMP ALL` each preset inherited the previous one's; every colour in the run was off. The
native host had always zeroed it in `ResetForDeterministicRun()`. Before the fix mode A
scored 0.8634 mean; after, 1.0000.

The lesson is the one the comment above `tlStepBegin()` already stated: *miss one piece of
host-side state and the diff quietly starts measuring whatever the host happened to be doing
before the run began.* When the two hosts disagree, suspect host state before shaders.

### Timelines

Two fixtures, both under `tests/fixtures/` and both listed in the parity config:

| Fixture | How it was made | Covers |
|---|---|---|
| `timeline-20s.json` | Recorded off the synthetic driver at seed `0x5F356495` | General: onsets, beat sweep at 112 BPM, bass modulation, chord changes |
| `timeline-novelty.json` | **Constructed** by `tools/make-timeline.mjs` from `timeline-novelty.script.json` | Fires `novelty` at 2 s / 7 s / 12 s / 17 s with peaks up to 1.0, plus a chord change and transient on the 12 s break |

The synthetic driver's RNG is **seeded** (`RNG_SYNTH_SEED`, matching `SyntheticDriver` in the
native host), so a recording is reproducible and `--seed N` draws a different one deliberately.
Recording still cannot *target* a rare event, though — novelty fires at `dt/22` per frame, so
20 s is a coin flip, which is why the coverage fixture is constructed rather than recorded.
Constructed timelines are regenerated from a committed script, so the script is the artifact to
read and edit:

```powershell
node tools/make-timeline.mjs --script tests/fixtures/timeline-novelty.script.json --summary
```

Verified: on `oil-wheel` the novelty pulses drive `decay` across 0.988–0.994 (the full
`+0.006` mapping range) and on `era-europe72` they drive `beatMorph` across 0.36–0.90 and
`cells` across 3.45–6.21, each correlating with `novelty` at r = 1.000. Mode A passes on this
fixture at 1.0000 mean / 0.9995 min.

## Audio and DSP

```powershell
.\run.ps1 --audio                          # visuals driven by whatever the machine plays
.\run.ps1 --audio --preset era-europe72

# capture real features for preset work in the browser
.\run.ps1 --record-features captures\my-capture.json --record-seconds 60
```

`--audio` opens WASAPI loopback on the default render endpoint, so it reacts to Spotify —
or anything else the machine is playing — with no OAuth, no API key and no account. The
synthetic driver is still the default, because it is the parity reference and it works with
no audio at all.

### Threading

Plan §6, as built. The render thread never blocks on audio and never touches the ring:

| Thread | Does | Never does |
|---|---|---|
| audio callback (NAudio) | convert to float, write the ring | allocate, log, lock |
| analysis (`AudioEngine`) | DSP one hop at a time, publish the frame | touch GL |
| render (`GlHost`) | read the newest published frame | wait on audio |

The hand-off is a **seqlock**, not a double buffer with a volatile index: a 224-byte struct
torn across two publishes would produce a Feature Frame that never existed — a beat phase
that jumps, a luminance step the safety limiter reacts to. Readers retry instead.

### Capture robustness

§2.3 lists four failure modes. All four are handled, and the first one is the reason the
render loop is never driven by the audio callback:

| Failure | Handling |
|---|---|
| No callbacks at all while the endpoint is idle | Nothing downstream is callback-driven; stale audio reads as silence via a timestamp, and the watchdog does **not** treat "no data" as a fault unless the session has already delivered audio. Getting this wrong reinitialized capture every two seconds on a quiet machine. |
| Default device changes | The watchdog compares the default endpoint's ID against the one we opened, every 250 ms. §2.3 asks for `MMDeviceEnumerator` notifications, but NAudio 3.0.1 declares `IMMNotificationClient` internal, so it cannot be implemented from outside the assembly; polling cannot be missed and needs no interop. |
| Format varies (44.1 / 48 / 96 kHz, float32 or 16/24/32-bit PCM) | Read from the device and converted in the callback. Every bin frequency derives from the actual rate — `Sample_rate_is_read_not_assumed` drives 44.1, 48 and 96 kHz through the same band-isolation assertions. |
| Exclusive-mode holder | Detected by HRESULT, reported as one status line, app keeps running; the watchdog keeps retrying so closing the offending app recovers on its own. |

### Feature recorder — the payoff

The native host writes real-audio Feature Frames in **the same timeline format the harness
reads**. Both sides already implemented it, so this was nearly free, and it changes how
preset work is done: capture a minute of actual Grateful Dead audio, load it in the browser
harness, and tune against real feature data instead of slider guesses.

`tests/fixtures/timeline-real-20s.json` is such a capture, and it is wired into the parity
config as a third timeline — the only one whose feature distributions are the real thing.

Recording runs on the **analysis** thread. Sampling the exchange from the render loop looks
equivalent and is not: this GPU renders this workload at ~22 fps while the DSP publishes at
93.8, so a render-driven recorder captured one row in four and still labelled the file
93.8 fps. The timeline replayed four times too fast and nothing in it said so.

### Cost against §11's 1%-of-a-core budget

Measured, Release, 20 s of audio, per analysis hop (93.75 hops/s at 48 kHz):

| | ms/hop | % of one core |
|---|---|---|
| Sustained loop (DspCostTests) | 0.087 | **0.81%** |
| In the running app, this machine | 0.195 | 1.82% |

The DSP's *work* is inside the budget. The gap to the in-app figure is **uniform across
every stage** — FFT, tempo, percussivity and levels all cost about 2.2× more there — so it
is not a hot spot: the analysis thread is idle most of the time and wakes to do a few
hundred microseconds of work, on a core with no reason to boost. Batching three hops per
wake recovered about 10% of it; the rest is the machine, and Phase 4 owns the perf gate.

Two stages were worth optimizing and were, by **decimation rather than approximation** —
identical values, computed less often, because both feed smoothers that are slower than the
hop rate anyway:

| Stage | Before | After | How |
|---|---|---|---|
| percussivity | 0.095 | 0.017 | every 4th hop (its own envelope is 120 ms), mask on every 2nd bin |
| tempo ACF | 0.056 | 0.015 | every 4th hop; the phase accumulator still advances every hop |

Watch for one trap if you change `TempoEvery`: the BPM median is sized by how often the
estimate is *produced*, not in hops. Sizing it in hops silently turned a 2 s median into an
8 s one and put the drift-tracking error back over budget.

### Where the DSP departs from §7, and why

Every one of these came out of a measurement, and the measurement is in the code comment
next to it:

| §7 says | Built as | Because |
|---|---|---|
| `confidence = peak / mean(acf)` | Peak of the **mean-removed, variance-normalized** autocorrelation | The ratio is tempo-biased: identical metronomes scored 3.62 at 60 BPM and 1.28 at 180, with no threshold able to accept 180 without also accepting arrhythmic clicks. As a correlation coefficient, click tracks land at 0.84–0.96 flat across the range and free time at 0.07. |
| BPM median over 8 s | 2 s | With the 4 s autocorrelation window, 8 s put the estimate ~7 s behind a drifting tempo — 3.16 BPM against §14's own ±2 BPM requirement. The two numbers are in tension and the drift case is the Dead case. |
| HPSS time window 5 | 17 | At a 512-sample hop, 5 frames is 53 ms — about the length of the drum hit it is meant to reject. A click filled the window and a click track measured *less* percussive than a pad. |
| `perc = sumP / (sumP + sumH)` | Per-bin mask, then the ratio of masked energy | Summing the two medians across the spectrum does not discriminate: both sums are dominated by the window's leakage floor, and a pad and a dense mix both measured 0.42. |
| chroma = `|X[k]|` with a log-frequency taper | Spectral **peaks** only, at parabolically interpolated frequencies | Summing every bin smears the vector across all twelve classes — a leakage skirt spans several bins, and one bin spans several semitones below 200 Hz. A C major chord came out C#/G#/A# and read as B-flat minor. |
| bands normalized to 0..1 | Same window, plus a fitted `v^4.66` calibration and a relative gate | Two structural problems. §7.1's envelope is a peak follower, so on dense material it sits at the top of its own window and every field read ~0.4 high — presets authored in the harness would be driven flat out. And the per-band normalizer amplifies its own noise floor: a pure 1550 Hz tone lit all eight bands to 0.93 until a gate, relative to the loudest band, suppressed the ones carrying only leakage. |
| `dynamics = peak - rms` | Crest factor in dB, normalized separately | Both inputs are already normalized against their own ranges, so on steady material both sit near the top and the difference is ~0 regardless of how peaky the audio is. |
| novelty = cosine distance | × 2.5 | Cosine distance between two non-negative 8-vectors is confined to a small part of 0..1; a hard cut measured 0.4 and steady material under 0.09, while presets route novelty expecting the full range. |

`NormalizationTests` is the guard on the last three. Its real assertion is the **aggregate**:
a systematic scaling error moves every field the same way, and that is the failure that
makes all 13 presets look wrong at once. Per-field tolerances are deliberately loose,
because a per-field difference between a synthetic audio signal and a slider default is not
evidence of anything.

### What is verified, and what is not

Verified here: 60 DSP tests green, including tempo lock at 60/90/120/132/150/180 BPM, a
click track drifting ±8% over 60 s tracked within 2 BPM after converging in 0.94 s, the
`bpmConfidence < 0.35` fallback engaging on 100% of frames of arrhythmic material with
beat phase anti-correlating at r = −1.000 with the onset envelope, key detection on five
triads, band isolation at three sample rates, and a real capture replaying in the harness.

**Not verified: actual Spotify audio, and an actual Dead jam.** This machine had nothing to
play, so the real-audio path was exercised with a generated WAV through the system mixer —
genuine WASAPI loopback, genuinely captured, but the material is synthetic. Two things
therefore remain open, and both need a human with a music library:

1. **Tempo lock on real music.** On dense material with onsets on every half-beat the
   autocorrelation prefers the subdivision, confidence stays under 0.35, and the visuals run
   on the onset fallback rather than a beat grid. That may be correct for Drums → Space and
   wrong for Sugar Magnolia; the click-track tests cannot tell the difference.
2. **Band ranges on real mixes.** The captured features run high (band means 0.70–0.82),
   which is above where the presets were authored. `LevelGamma` was fitted against a
   synthetic signal; refit it against a real capture before deciding the presets need
   retuning.

---

## Non-negotiables

Carried from the build plan. These are gates, not aspirations.

1. **Zero heap allocation in the render loop.** Enforced by a zero-Gen0-collection assertion in
   the Phase 4 budget test. This is what catches an accidental LINQ expression in a hot path.
2. **The safety limiter applies unconditionally** and is not preset-overridable below its floor.
   This runs where anyone can walk past it. See build plan §12.
3. **Three-tier metadata degradation.** Song mode → era → audio-only, each independently good.
   The app must run fully with no metadata at all — metadata failure never takes down rendering.
4. **No Spotify Web API dependency.** See build plan §2. Metadata comes from Windows SMTC.
