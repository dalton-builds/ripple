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
- [ ] **2** — WASAPI loopback + full DSP. **Gate is 3 of 4.** Flux, onset and parity closed
  against measurement. **Levels** is the one open item: three real captures now exist and
  their per-band fits disagree by up to 2.37×, so no coefficients are committed. Tempo moved
  off the blocking list — the real captures showed its worst failure was an artefact of the
  synthetic fixture. See "What the first real captures said".
- [x] **Motion sync** — the beat grid now drives the image. Rhythm moved out of global
  luminance into geometry, phase fields pulse-shaped and confidence-gated, visual envelopes
  scaled in beats. See "Motion sync" for which routes measured a real effect and which did not.
- [~] **3** — SMTC behind `IMetadataSource`, §9.2 parsing, era mapping, song modes, the
  now-playing caption, and **auto-capture** so fixtures accumulate from listening. Built and
  unit-tested; the parts that need a human playing Spotify are listed under "What is verified".
- [x] **4** — Governor control loop, idle states, safety limiter with master intensity,
  budget gate green (p99 5.11 ms, zero Gen0). Soak started but never ran a full 8 hours.
- [~] **5** — Desk-prototype slice tagged `prototype-v1`, plus the **standalone build**:
  single-file self-contained publish, verified running on a clean path with no SDK. The full
  preset library is still open.

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
node tools/extract-shaders.mjs --check        # shaders/ vs the harness
node tools/extract-defaults.mjs --check       # presets/_defaults.json vs the harness
node tools/check-presets.mjs                  # presets/eras/*.json vs the harness
node tools/check-harness.mjs                  # does the harness still run at all?
node tools/parity-gate.mjs --mode a           # dump both hosts, compare, exit nonzero on fail
node tools/parity-gate.mjs --mode a --all-timelines
node tools/parity-gate.mjs --mode b           # informational curve
```

`check-harness.mjs` answers a different question from the other three: not "do the two copies
agree" but **"does the harness still run at all"**. It loads the page in headless Chrome, fails
on any uncaught exception or console error, and pushes every mapping source named by any preset
through `rhythmicShape`. It exists because the harness has now been broken twice by careless
text edits, each time in a way no static check caught — a backtick inside a shader comment
terminated the template literal and surfaced as `ReferenceError: gl is not defined` from an
unrelated block, and a quoting accident turned `key === 'barPhase'` into `key === barPhase`,
which parses fine and throws only when a preset routes that source. Both were found by the
parity gate, twenty minutes and one unhelpful "harness dump failed" later.

One detail in it is load-bearing: it navigates to the page **after** attaching the debugger,
rather than passing the URL on Chrome's command line. Boot-time exceptions thrown before
`Runtime.enable` are never reported, which is exactly the class of failure it exists to catch.

**Run the four `--check` tools before the gate.** They compare the things that exist twice —
once embedded in `harness/index.html` and once on disk — and they fail in seconds where the
gate takes twenty minutes to fail less informatively.

`check-presets.mjs` was added after the era presets drifted and cost a full gate run. The
motion-sync pass added four rhythmic mapping routes to `presets/eras/*.json` and not to the
harness's embedded `ERA_PRESETS`, and the gate went from mean SSIM 1.0000 to **0.9493 with a
min of 0.4674** — with no hint of the cause, because SSIM cannot say "your preset tables
disagree". `compare-trace.mjs` named it in one run (`warpAmount` 0.4016 against 0.1807), which
is why "run the trace diff first" is the documented move on a parity failure. The check now
closes the hole the other two `--check` tools left: shaders were guarded, `PRESET_DEFAULTS` was
guarded, and the presets themselves were not.

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

Re-measured in Phase 4 with the app **actually rendering** and competing for the core:
**0.182 ms/hop, 1.70% of one core**, 3184 hops, 0 samples dropped. Adding a render loop did
not make it worse than the 1.82% figure above.

### Where §11's footprint budget departs from the plan, and why

Same rule as the DSP table: the number moved because a measurement said so, and the
measurement is here. This is the one plan figure Phase 4 retired.

| §11 says | Graded as | Because |
|---|---|---|
| **working set** under **60 MB** | **private bytes** under **128 MB** | Both halves were wrong, for two independent reasons. |

**60 MB is unreachable, and not because of the render loop.** Working set before a single
frame is drawn measured **137.0 MB** on Intel UHD / OpenGL 4.3 — .NET runtime images, the GL
ICD's mappings, GLFW and NAudio — against a managed heap of **1.8 MB** and 18 MB of growth
across a whole 60 s run. There is no change to the frame loop that moves a floor the frame
loop does not set. Nothing was optimized to meet this; nothing could be.

**Working set is the wrong metric anyway.** It is elastic: Windows trims and regrows it
against whole-machine pressure, so it is not a property of this process. Sampled over three
minutes of an idle soak it wandered **218 → 205 → 179 → 172 MB** — a 46 MB swing — while
private bytes sat at 117 MB and the handle count did not move. Graded on working set that
run "leaked 46 MB and gave it back"; graded on private bytes it was flat. A metric that
reports a leak where there is none cannot also report a leak where there is one.

So 128 MB is the settled private-bytes steady state (~117 MB) with headroom, and it is not a
number picked to make the gate green — the gate now grades two things the old one could not:

- **private bytes < 128 MB**, absolute;
- **drift < 5 MB over 8 hours**, from a baseline latched at 20 minutes.

The 20-minute latch is itself measured. The GL driver allocates lazily well past startup —
an 11 MB step at 5 min and a further 27 MB at 15 min, then flat — and counting that as drift
would fail every run for a reason unrelated to a leak. `--budget-private MB` rebases the
ceiling from the command line rather than by editing a constant.

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
| bands normalized to 0..1 | The **percentile** of each band within its own 45 s history, faded to zero below 6 dB of p5–p95 spread | Three faults, and the third retired the first fix. §7.1's envelope is a peak follower, so on dense material it sits at the top of its own window and every field read ~0.4 high; and the per-band normalizer amplifies its own noise floor, lighting all eight bands to 0.93 on a pure 1550 Hz tone. The first repair was that window plus a fitted `v^gamma`, and **that exponent cannot be fitted** — see below. Percentile mapping needs no target median to aim at, so there is no constant to fit: it took the aggregate |delta| against the synthetic driver from 0.184 to 0.122, and it holds bands identical to three decimals across a spectral-balance change that takes `air` to 0.000. The 6 dB floor survives as a spread gate, because the percentile of a value inside a 0.4 dB distribution is still 0..1 and would still expand noise into motion. |
| `dynamics = peak - rms` | Crest factor in dB, normalized separately | Both inputs are already normalized against their own ranges, so on steady material both sit near the top and the difference is ~0 regardless of how peaky the audio is. |
| novelty = cosine distance | × 2.5 | Cosine distance between two non-negative 8-vectors is confined to a small part of 0..1; a hard cut measured 0.4 and steady material under 0.09, while presets route novelty expecting the full range. |
| `flux` normalized against its own range | 55 ms envelope, then an adaptive window whose hi/lo track the **envelope** and not the raw input, clamp applied last | Two structural faults, and the field measured p50 0.000 / p90 1.000 on real audio — two spikes, nothing between. The divisor was the onset comparator's own threshold, so it saturated on every onset and duplicated `F_ONSET`; and raw flux is half-wave rectified, so it is ~0 wherever the spectrum falls (42% of frames on dense material, 91% on a click track). Tracking the window on the raw extremes while the numerator is smoothed then capped it at p90 0.229. |
| 50 ms onset refractory alone | Refractory **plus** a post-onset bar at 3.5× the firing peak, decaying at 130 ms | The refractory is four times shorter than the 150 ms envelope decay, so a detection on the shoulder of a peak re-armed the impulse before it had fallen. Measured 13.5 onsets/s against 3.6 genuine event times, holding `F_ONSET` at mean 0.762 — and `AdvanceMotifs` gates bolts on `onset > 0.82`, so the gate was armed on 40% of frames and motifs fired on their cooldown. A multiplier **below** 1 cannot fix this: if the bar starts under the peak that armed it, an equal peak one hop later already clears it. Above 1 the minimum spacing becomes `tau·ln(mult)` = 163 ms, while a genuinely stronger hit still breaks through. |

**Why `v^gamma` was retired rather than refitted.** It was measured to be unfittable, not
merely untuned. The exponent had to be calibrated per band against real material, and real
material does not agree on what the answer is: the `air` band's median on a 1972 Veneta
board tape is **exactly 0.0000**, against **0.120** on a 1985 release of the same song — a
desk feed from 1972 has no content above 10 kHz at all. The exponent that maps 0.0000 onto
the synthetic driver's 0.212 is 0.11; the one that maps 0.120 is 3.41. That is a 30x
disagreement, and **no number of further captures narrows it**, because a band with nothing
in it carries no information about how to scale it. The mids, by contrast, agreed to within
1–4% across those thirteen years — the failure is specific to bands the recording chain
did not capture, and those are exactly the bands a Dead archive is full of.

Percentile mapping does not ask the question that has no answer. It asks where a value sits
in what the band has been doing lately, which is defined for every distribution, and a band
with no content has no spread and is reported as static instead of demanding an undefined
correction. The cost of that is one decaying 1 dB histogram per field: the DSP went from
**1.70% to 1.76% of one core** against its 2% budget, spent almost entirely in `bands`
(0.0051 → 0.0132 ms/hop).

The window length is the only tuning parameter left, and 45 s is set by the shape of the
material rather than by taste: verse-to-chorus dynamics run 20–30 s, so the window must be
longer than that for a quiet verse to read as quiet, while a Dark Star arc runs several
minutes, so it must be far shorter than that for the arc to still read as movement.
`NormalizationTests` is the guard on the last three. Its real assertion is the **aggregate**:
a systematic scaling error moves every field the same way, and that is the failure that
makes all 13 presets look wrong at once. Per-field tolerances are deliberately loose,
because a per-field difference between a synthetic audio signal and a slider default is not
evidence of anything.

## Dead intelligence layer

§9. What makes it a talking point rather than a screensaver: the machine demonstrably
understands what it is listening to.

### Auto-capture — fixtures as a byproduct

The five fixtures §14 depends on blocked three development cycles, and the reason was never
technical. Capturing one was a separate deliberate act: stop, run a wrapper, play the right
thing for sixty seconds, check the take. Nobody does that spontaneously, so it never happened,
and the tempo octave and the per-band gamma stayed unresolvable for want of material that
ordinary listening would have produced by the dozen.

Testing SMTC means playing Spotify anyway, so every track change is already a capture
opportunity. On a new track while playing, Ripple records 60 s of Feature Frames named and
labelled from the metadata:

```
tests/fixtures/real/auto/grateful-dead-dark-star-live-dead.json
```

Four rules keep it from being a nuisance, and each is a decision rather than a default:

- **never twice** — the slug includes the album, so the same song on two releases is two
  captures because it is genuinely two pieces of material;
- **a total disk cap** (200 MB by default) that **stops cleanly rather than pruning**. These
  are regression baselines; deleting someone's fixture to make room for a new one is the
  opposite of what they are for;
- **only while playing**, so a paused track does not record a minute of silence;
- **a pause mid-capture abandons it.** A partial capture is worse than none — it sits in the
  fixture folder looking like a valid baseline while being twelve seconds of one chorus.

`metadata.autoCapture` in the config, **on by default during this phase**. Turn it off once
there are enough. Outside a repo tree, captures land in `%APPDATA%\Ripple\captures\auto`.

### SMTC

`Windows.Media.Control`, session filtered to Spotify by `SourceAppUserModelId` — matched as a
substring, so the desktop build (`Spotify.exe`) and the Store build
(`SpotifyAB.SpotifyMusic_…!Spotify`) both hit. Subscribed to `MediaPropertiesChanged` and
`PlaybackInfoChanged`, **plus a 2 s poll**.

The poll is not a belt-and-braces afterthought, it is the mechanism: §9.1 says SMTC events are
occasionally missed and they are. The events are treated as a latency optimisation over the
poll, and both handlers do nothing but wake the loop. Everything works with the events deleted;
they only make it feel instant.

Three properties of the API shape the implementation:

- **it is async and WinRT, and the render thread is neither.** Every call goes through a
  bounded wait on a background thread and the result is latched into a field the renderer
  reads. The render thread never awaits;
- **it can fail to load entirely** — a machine without the projection, or a sandbox. That must
  be indistinguishable from "nothing is playing";
- **a session can vanish mid-call.** One bad tick must not end the thread.

All of it sits behind `IMetadataSource`, and `NullMetadataSource` is a first-class
implementation rather than a test double: it is what runs when SMTC is unavailable, when the
user turns metadata off, and when the projection throws. **Metadata failure never takes down
rendering** is enforced structurally — every field nullable, every entry point guarded.

**Pause is instant.** §11's idle is RMS-based and needs three seconds of silence to conclude
anything; `PlaybackStatus == Paused` reaches the same conclusion immediately. The difference
is between "it noticed" and "it reacted".

### Parsing

`TrackParser` is a **pure function of three strings** plus the venue book — no SMTC types, no
GL, no clock. That is deliberate: it is the only part of Phase 3 testable without playing
music, and it is where a wrong answer is most expensive. `TrackParserTests` runs it against a
fixture list of real Spotify strings, including the messy ones.

**The governing rule is that a failure degrades, never guesses.** A wrong date picks a wrong
era, and someone who knows the catalogue sees a 1972 palette on a 1989 show. So every
extraction may return nothing, and nothing is a fully supported state.

| | |
|---|---|
| artist gate | the extended family, matched as a normalized substring — and a **not-family list checked first**, because "Dead Kennedys", "Dead Can Dance" and "deadmau5" all pass a naive test and the gate exists precisely to keep them out |
| dates | ISO, M/D/YY, `'72`, bare year — album before title, because the release carries the show and a title that happens to contain a year is weaker evidence |
| series | Dick's Picks, Dave's Picks, Road Trips, the Vault releases, Cornell — **kept in `data/venues.json`, not in code**, per §9.2 |
| normalization | strips `- Live at …`, `[Live]`, `(2013 Remaster)`, `Pt. 1`, trailing dates |
| segues | `>` and `->` split; the first song drives the preset, the rest are recorded |

Two normalization details that were bugs first: **apostrophes are deleted, not replaced with a
space** — "Franklin's Tower" has to reach `franklins tower` to match `data/songs.json`, and
`franklin s tower` matches nothing — and **`&` becomes "and"**, or `Dead & Company` never
reaches the family list.

A parsed date always beats a series' default date, so `Dick's Picks Vol. 8: 1970-05-02` uses
the real show rather than the series.

### The caption

§8.5's one piece of chrome that makes a passerby stop. Fades in over 8 s, bottom-left:

```
Dark Star · Grateful Dead · Veneta, OR · 8/27/72
```

Its whole job is to be **read from three feet**, which drives every decision. A real system UI
face at 15 px, hinted — §8.5 is explicit that a psychedelic display face defeats the purpose,
because an unreadable caption conveys nothing. A soft dark shadow underneath, because what is
behind it is a high-contrast animated fractal and white-on-anything is otherwise a coin flip.
And **drawn after the composite**, so it is never part of the luminance §12's limiter measures
— chrome that could make the picture flicker would be a safety violation caused by decoration.

Legibility is a length constraint before it is a typography one, and that is a test. Built from
the raw title the caption came out at 140 characters with the venue and the date in it twice:

```
Dark Star - Live at Old Renaissance Faire Grounds, Veneta, OR, 8/27/72 · Grateful Dead ·
Old Renaissance Faire Grounds, Veneta OR · 8/27/72
```

So the title is cleaned for display separately from the song-mode key, and the venue is
shortened to its town — `Barton Hall, Cornell University, Ithaca NY` → `Ithaca, NY`. A bare
year never appears: a year parsed off a studio album is not a show date, and printing `· 1970`
beside a song title implies a precision that is not there.

### Three-tier degradation

§9.4's chain is the gate, not a fallback path. Each tier has to look intentional alone:

| tier | when | result |
|---|---|---|
| **song mode** | title matches `data/songs.json` | mode merged recursively over its era |
| **era** | a date parsed | one of `presets/eras/` |
| **audio-only** | neither, or not the family | `era-unknown`, driven entirely by the DSP |

Merge order is CONTRACTS §4 and is not negotiable: **era → song mode → user config → governor
clamp**, governor last and absolute. A song mode is a *partial* override — a mode naming only
`feedback.decay` keeps every other authored value, which is what lets `data/songs.json` be 20
lines instead of 20 full presets. "User config" includes the preset lock: someone who pressed
`L` has said what they want, and metadata does not overrule it (the caption still shows, because
the lock is about the look).

Measured on the three cases §9.4 names:

```
live    era=2  date=1972-08-27  song='dark star'  Dark Star · Grateful Dead · Veneta, OR · 8/27/72
studio  era=7  date=none        song='ripple'     Ripple · Grateful Dead
other   era=7  date=none        song=''           Ripple · Some Other Band
```

Three different, intentional states — and the third is the one the artist gate earns: same
title, same year in the album, and it correctly refuses to treat it as a Dead show.

**Tier 2 was reported as unexercised twice, and that is now fixed at both levels.** The table
above came from the parser; what had never run was the rest of the chain — the director's
`Resolve`, the preset lookup, the caption — for a track that is the family with *no* date.
Tiers 1 and 3 turn up within minutes of ordinary listening; tier 2 needs a studio cut, and
across two sessions one never came up on shuffle.

- `DegradationTierTests` pins all three through the **real** parser, venue book and preset
  store rather than stubs, because the failures that matter live in the data files: a preset
  id that is not on disk resolves to a fallback index and looks like it worked. It passes a
  sentinel `fallbackIndex` of `-999` so a fallback return is visible instead of plausible.
- `--meta-script "Title|Artist|Album;..."` drives the metadata layer from the command line
  through the same seam SMTC uses, so a tier can be rehearsed on demand. It is a diagnostic:
  the HUD reports `scripted` and the log says so, because a rehearsed caption must never be
  mistaken for an observed one.

Live, in the running app, all four scripted tracks in order:

```
Dark Star / Grateful Dead / Sunshine Daydream (8/27/72)
  -> song mode 'dark star' over era-europe72        [1972-08-27] era 2   tier 1
Sugar Magnolia / Grateful Dead / American Beauty
  -> song mode 'sugar magnolia' over era-unknown    no date,    era 7   tier 2
Ripple / Grateful Dead / American Beauty
  -> song mode 'ripple' over era-unknown            no date,    era 7   tier 2
Blue Monday / New Order / Power Corruption and Lies
  -> audio-only (not the family)                    no date,    era 7   tier 3
```

The tier-2 caption is **`Sugar Magnolia · Grateful Dead`** — venue and date fields absent
rather than empty, which is the thing worth asserting: the line is assembled by joining a
list, so an unconditional add of a null venue would print `· ·`.

Two things the exercise found:

- **`IMetadataSource` was not actually a seam.** The director subscribed to track changes
  behind `source is SmtcMetadataSource`, so any other implementation could never deliver a
  track no matter what it reported — including the `NullMetadataSource` the file's own comment
  calls "a first-class implementation rather than a test double". The events are now on the
  interface and the type check is gone.
- **A log line claimed a source that was not running.** `[meta] SMTC watching for Spotify` was
  printed unconditionally after `Start()`, so it appeared even on the fallback path taken when
  the projection fails to load and nothing is watching at all. It now reports the source's own
  status and says `running audio-only` when the source is unavailable.

One departure from §8.5 worth naming: its example caption is `Dark Star · Veneta, OR · 8/27/72`,
three fields and no artist. The artist is carried anyway — on a bootleg the band name is the
part a listener cannot infer from the title — so tier 1 shows four fields.

### `tempoLock` is provisional

Song modes with `"tempoLock": true` — `drums` is the shipped one — inherit the **unresolved
tempo octave**. The tracker currently reports the subdivision confidently on dense material, so
a mode that locks to the grid locks to a grid that may be 2× wrong. Implemented as specified,
flagged here, and revisit once the auto-captures validate the tracker.

**Beat-relative time constants carry the same caveat.** They are implemented (`HostState.
BeatScale`, `60/BPM`, clamped to [0.5, 2.0], blended by confidence) and they were shipped in the
motion-sync pass — but the confidence blend only protects against a grid the tracker *distrusts*,
and the open bug is a grid it is confident and wrong about. Same fix closes both.

### Standalone build

```powershell
.\build.ps1 -Publish
```

Single-file, self-contained, win-x64, ReadyToRun, **no trimming and no `PublishAot`** — CsWinRT
plus AOT is a known hazard and the footprint is already inside budget. Output is one exe plus
`glfw3.dll` and loose `shaders/`, `presets/`, `data/` folders, which stay loose so they keep
hot-reloading.

Two things cost a publish cycle each, and both only reproduce in a published build:

- **`IncludeNativeLibrariesForSelfExtract` must be `false`.** Bundled natives are extracted to a
  temp directory that Silk.NET's own resolver does not search, and the exe dies with
  `PlatformNotSupportedException: Couldn't find a suitable window platform (GlfwPlatform - not
  applicable)` while the identical build run from `bin/` works — because `bin/` has `glfw3.dll`
  sitting beside it;
- **the GLFW platform is registered explicitly.** Silk.NET discovers its backend by scanning
  loaded assemblies, and that scan does not survive single-file bundling.

Verified by copying the publish folder to a directory outside the repo and running it with a
bare `C:\Windows\system32` PATH and no `DOTNET_ROOT`.


## Motion sync

The DSP produced a beat grid from Phase 2 onward and almost nothing used it. The §10 mapping
tables were ten smooth envelopes each — `sub`, `bass`, `lowMid`, `mid`, `presence`, `air`,
`stereoWidth`, `keyHue`, `novelty`, `percussivity` — with no rhythmic term in any of the 13
presets. `beatPulse()` and `barPulse()` were called, but only in places that could not carry
the effect: a 0.0018 zoom term in `warp.frag`, against a base zoom excursion around 0.011.

### Beat response goes through geometry, never luminance

This is the constraint the whole layer is built around, and it is a test
(`RhythmicRoutingTests.Beat_response_is_not_wired_into_brightness`) rather than a comment,
because a brightness pulse on the beat looks obviously right and is self-defeating in place.

`warp_fbm.frag` used to end with:

```glsl
col *= 0.45 + 0.95 * F_RMS + 0.55 * beatPulse(4.0) * F_BPM_CONF
     + 0.65 * F_ONSET * F_PERCUSSIVITY;
```

A rhythm-driven brightness swing of up to 5.8×, applied to the global exposure. §12's limiter
caps mean frame-luminance delta at 0.08 per frame and backs off above 3 Hz — so that pulse was
clamped, `SafeScale` dropped, and the limiter **dimmed the whole image in response to the
beat**. The louder the rhythm, the more the safety system fought it.

The rhythmic terms moved into geometry, which is not rate-limited:

```glsl
p *= uScale * (1.0 - 0.12 * F_SUB - 0.030 * beatPulse(3.0) * F_BPM_CONF);
p = rot2(0.018 * t + 0.35 * F_STEREO_WIDTH + 0.06 * barPulse(2.0) * F_BPM_CONF) * p;
float amt = uWarp * (0.65 + 1.15 * F_BASS + 0.45 * F_ONSET * F_PERCUSSIVITY);
col *= 0.45 + 1.30 * F_RMS;     // level drives exposure; rhythm drives geometry
```

Under a long feedback decay a geometric pulse smears into the trails instead of clipping, which
is how MilkDrop reads as beat-synced. The feedback decay was **not** shortened to compensate.

### Phase fields are ramps, and have to be shaped

`beatPhase` is 0 **on** the beat and climbs to 1 just before the next one. Routed raw it makes
a parameter drift up and snap back — a sawtooth, which reads as a glitch on the beat rather
than a pulse on it. `RhythmicShape()` in the native host and `rhythmicShape()` in the harness
convert both phase fields to `pow(1 - phase, sharpness)`, the same curve `features.glsl`
already uses shader-side.

Both are scaled by `bpmConfidence`, so **a grid the DSP does not trust drives nothing.** That
matters more than it looks: below §7.3's floor the DSP is already synthesising `beatPhase` from
the onset envelope, and a full-strength pulse on a guessed grid is worse than no pulse. Scaling
rather than switching keeps the transition a fade rather than its own visible event. It also
gives a free A/B — zeroing the `bpmConf` column of a timeline disables the entire rhythm layer
and changes nothing else, which is how `captures/ab/` is built.

### Which routes worked

Measured with `tools/rhythm-depth.mjs`, which correlates each traced draw scalar against the
shaped drive that is supposed to be moving it. `era-europe72` over the 60 s budget timeline,
3600 frames, `bpmConf` mean 0.880:

| route | parameter | swing above baseline | depth | corr | |
|---|---|---|---|---|---|
| `onset → warpAmount` | warpAmount | 0.195 → 0.536 | 103% | 0.917 | **works** |
| `beatPhase → feedbackZoomPulse` | zoom | 0.0068 → 0.0152 | 83% | 0.962 | **works** |
| `flux → fractalMorph` | beatMorph | 0.380 → 0.765 | 72% | 0.380 | works |
| `flux → fractalMorph` | cells | 3.60 → 5.61 | 45% | 0.429 | works |
| `barPhase → paletteRotate` | palRot | 56.6 → 1367.6 | 182% | **−0.028** | **retired** |

`onset → warpAmount` is the strongest: the churn more than doubles on a transient and tracks
it at r = 0.917.

`beatPhase` needed a new target. Routed through the existing `feedbackZoom` it moved zoom from
1.0067 to 1.0111 — a 0.4% swing, correlated at r = 0.851 and invisible, because that target's
gain (0.0055) is sized for a band sitting near 0.5 all the time and a pulse is only high for a
fraction of a beat. `feedbackZoomPulse` uses 0.012, putting it alongside `warp.frag`'s own
sub-bass term rather than a fifth of it. Raising `feedbackZoom` instead would have changed the
authored bass response of all 13 presets to buy one route.

`flux → fractalMorph` works but correlates only moderately, because `novelty` already writes
that target. Worth knowing while you are there: `novelty` sits at p50 0.039, and
`fractalMorph` applies `beatMorph *= 0.6 + 0.9v`, so the existing `novelty → fractalMorph`
route is a near-constant 0.64 multiplier — it is suppressing morph, not modulating it.

`barPhase → paletteRotate` was **removed rather than left in place**. `paletteRotate` does
`palRot += v` and `palRot` accumulates without bound — `keyHue` drives it to the hundreds
within seconds, so a bar pulse peaking at 1.0 is noise against it, and r = −0.028 says exactly
that. A dead route in a config file is a trap for whoever reads it next. Making it work means
changing `paletteRotate` from a rate into a position, which moves every authored palette
behaviour and should be done deliberately against real captures.

### Beat-relative time constants

Visual decay envelopes scale by `60/BPM` rather than sitting at fixed seconds, so a pulse
resolves in the same musical time at any tempo. A fixed 155 ms bolt tail is long gone before
the next beat at 80 BPM and still visible when it lands at 170.

`HostState.BeatScale()` — mirrored as `beatScale()` in the harness — returns the factor, and
two properties matter:

- **Clamped to [0.5, 2.0].** A bad tempo reading must not turn a 155 ms tail into something
  measured in seconds.
- **Blended by confidence back to the authored constant.** A beat-relative envelope on a grid
  the DSP does not trust is a decay time derived from a guess. At zero confidence, tempo has no
  effect at all — asserted, because that is the property that makes it safe.

The reference tempo is 104 BPM: 1.15 s of rose decay is about a bar and a half there, and
155 ms of bolt about a sixteenth, which is what the fixed constants were authored against.

### Seeing it, and the A/B that proves it

```powershell
.\scripts\capture-rhythm-ab.ps1 -Preset era-terrapin -Frames 90 -Stride 3
```

The control is not a second build. Everything derived from the beat grid is scaled by
`bpmConfidence` — the phase routes through `RhythmicShape`, and `warp_fbm`'s own scale and
rotation pulses through `F_BPM_CONF` — so a timeline with its `bpmConf` column zeroed turns all
of it off and changes nothing else. Same features, same presets, same shaders, same binary.

Rendered over 90 frames of the 124 BPM timeline, `era-terrapin`, rhythm-on against rhythm-off:

| | |
|---|---|
| SSIM mean | 0.8521 |
| SSIM min | **0.3102** |
| divergence peaks | frames 3, 45, 75 — **30 frames apart** |

A beat at 124 BPM is 29 frames. The two renders diverge most on a beat-period cadence and
re-converge between beats, which is what beat sync looks like measured rather than described.
Frames land in `captures/ab/rhythm-on` and `captures/ab/rhythm-off`.

It isolates the grid-derived response **specifically, not the whole rhythm layer**, and the
difference is deliberate: `onset → warpAmount` and `flux → fractalMorph` are not
confidence-gated, because they read features directly rather than a grid. Measured, they come
out bit-identical across the A/B — only `zoom` moves, depth 76% → 5% and correlation with the
beat 0.999 → 0.025. That is the property worth having: in a free-time passage where the
tracker gives up, the churn and the morph keep responding to the audio and only the grid
stops.

### What the first real captures said

Auto-capture produced four 60 s fixtures from ordinary listening, which is the first real
material this project has ever had. They settled one long-running argument, settled a second in
a way nobody expected, and corrected something this README previously asserted.

| capture | tier | era | grid |
|---|---|---|---|
| Grateful Dead — Sugar Magnolia, Veneta OR **8/27/72** | song mode over era | 2 (Europe '72) | 74.8 BPM, locked 13.5% |
| Grateful Dead — Sugar Magnolia, Merriweather **6/30/85** | song mode over era | 4 (Brent) | 80.5 BPM, locked 3.6% |
| Vince Gill — Don't Let Our Love Start Slippin' Away | audio-only (not the family) | 7 | 74.6 BPM, locked 23.5% |
| Eagles — Take It Easy (2013 Remaster) | audio-only (not the family) | 7 | 137.3 BPM, locked 38.3% |

The two Dead captures are **the same song, thirteen years apart**, which turned out to be worth
more than any third genre would have been.

#### The octave: MusicLike was the problem, exactly as suspected

`tools/acf-octave.mjs` autocorrelates a captured onset envelope and reports the correlation at
the beat against the correlation at half that period — the measurement the whole "stop
iterating" argument rested on.

| material | `acf[beat] / acf[subdivision]` | ACF peak |
|---|---|---|
| `MusicLike` (synthetic) | **< 0.12** | — |
| Sugar Magnolia '85 | **0.785** | 0.094, a weak grid |
| Sugar Magnolia '72 | **5.05** | 0.181, a usable grid |
| Take It Easy | **10.8** | 0.174, a usable grid |
| Don't Let Our Love… | subdivision **negative** (−0.227) | 0.324, a clean grid |

**Real drums invert it.** The structural objection — *no reweighting of the ACF can recover a
peak that is not there* — was true of `MusicLike` and is false of every real capture. On the
Vince Gill track the correlation at half the period is actively **negative**, which is what a
backbeat looks like: the envelope alternates strong/weak, and nothing could mistake the octave.

**But the failure mode that motivated the fix has not reproduced.** `MusicLike` at 88 BPM read
176.67 *at confidence 0.88* — confidently wrong, which is the only state that matters, because
a wrong octave below §7.3's floor drives nothing. On real material:

- Take It Easy read **137.3 against a true ~138** — correct, at the highest lock rate of the four;
- Don't Let Our Love… read 74.6 on a clean grid whose subdivision is negatively correlated;
- **both Sugar Magnolias read ~75–80**, thirteen years and two completely different recordings
  apart, and on the 1972 capture the correlation at that period is **5× stronger** than at half
  it. Two independent recordings agreeing that precisely is not an error mode.

That last row changed the conclusion. The first read looked like "80.5 where the song is roughly
160, so it read half" — a wrong octave. With the second Dead capture it is clearly not that: the
tracker is locking the **half-note pulse**, consistently, and the ACF says that is genuinely the
dominant periodicity in the onset envelope for this arrangement. Whether the half-note or the
quarter-note is "the beat" of Sugar Magnolia is a question about metrical level, not a defect —
and a visualizer breathing on the backbeat rather than on every quarter is arguably the better
answer anyway.

Where the tracker *was* least certain — Merriweather '85, a weak grid at ACF peak 0.094 — it
locked on **3.6% of frames at mean confidence 0.047**, so §7.3's fallback carried the whole
minute. That is the design working: least confident exactly where the material is muddiest.

So `CombDoublingEnabled` stays **false** and the tempo prior stays at **sigma 0**. Neither has
a case to fix in this data: the one wrong reading came with confidence low enough that §7.3
already handled it, and turning either on risks the drifting-click-track regression that
retired the comb in the first place. `tempoLock` song modes stay **provisional** — not because
the tracker is broken, but because no capture yet shows it locking *confidently* on Dead
material, which is the condition `tempoLock` depends on.

Beat-relative visual time constants are already implemented and confidence-gated, and this data
is the first evidence that the gate is the right shape: on the only capture where the octave
was wrong, confidence was 0.047, so the envelopes stayed on their authored constants.

#### The per-band gamma still does not converge, and now it is clearer why

`tools/fit-band-gamma.mjs` fits each capture and reports the **spread across captures**, which
is the result — a single fit always succeeds and proves nothing.

Across all four captures every band exceeds the 20% tolerance, worst 76%. But mixing a Nashville
studio record into the sample was never going to prove anything, so the interesting run is the
**two Dead captures against each other** — same band, same song, different decade:

| band | min | mean | max | spread | |
|---|---|---|---|---|---|
| hiMid | 6.90 | 6.92 | 6.95 | **1%** | agree |
| lowMid | 7.13 | 7.22 | 7.31 | **2%** | agree |
| mid | 7.16 | 7.29 | 7.42 | **4%** | agree |
| rms | 7.43 | 7.92 | 8.41 | 12% | agree |
| bass | 5.08 | 5.55 | 6.02 | 17% | agree |
| presence | 6.52 | 7.18 | 7.85 | 19% | agree |
| brilliance | 2.55 | 3.06 | 3.58 | 34% | fail |
| sub | 2.73 | 3.38 | 4.03 | 38% | fail |
| **air** | 0.11 | 1.76 | 3.41 | **187% (30×)** | fail |

Six of eleven agree, four of them to within 4%. **And the three that fail are exactly the bands
whose content depends on the recording medium rather than on the music.**

The air band settles it. Its median on the 1972 Veneta board tape is **exactly 0.0000** — a
1972 desk feed has no content above 10 kHz at all — against 0.120 in 1985. The fit tries to map
zero onto the synthetic driver's 0.212 and produces an exponent of 0.11 against the other
capture's 3.41. That is not a sampling problem and no number of further captures will improve
it: **you cannot calibrate a band that has nothing in it.** Meanwhile `mid` reads 0.538 and
0.526 across those same thirteen years, which is why the middle of the spectrum agrees to 4%.

So **the means are still not committed**, and the reason is now specific rather than statistical.

**This corrects an earlier claim in this file.** From the synthetic probe it said "both proxies
agree every band needs more than 4.66". Real material disagrees: `sub`, `brilliance` and `air`
all fit below the committed scalar. The direction was wrong, not just the magnitude.

**What would actually resolve it.** Not more captures — the mids are already settled and the
extremes are unfittable in principle. Two candidates:

- **Commit the middle and leave the edges alone.** `bass` through `presence` agree well enough
  to justify their means today; `sub`, `brilliance` and `air` keep the scalar. Defensible, and
  honest about which parts of the spectrum the calibration actually knows about — but it is a
  patch over a mechanism that does not fit the problem.
- **Replace the window-plus-curve with a percentile mapping.** Track each band's own
  distribution and map a value's position in it onto 0..1 directly. Self-calibrating by
  construction, no exponent at all, and a band with no content maps to a flat zero instead of
  demanding an undefined correction. This is the one that addresses the cause: the exponent only
  has to be large because §7.1's peak-follower envelope has already compressed the range
  upstream, and how much it compresses is a property of the recording. A calibration whose
  correct value depends on the tape is not a calibration.

**The second was taken.** `PercentileNorm` replaced the window-plus-exponent, the fitted
constants are gone rather than refitted, and the deviation table above records the measurement
that retired them. What it bought, on the property the exponent could never deliver: across a
spectral-balance change severe enough to take `air` from 0.487 to **exactly 0.000**, the bands
the change did not touch move by less than 0.002 — `sub` 0.396/0.396, `bass` 0.468/0.468,
`lowMid` 0.610/0.611. Medians land at 0.30–0.61 with nothing aimed at them, and the aggregate
|delta| against the synthetic driver fell from **0.184 to 0.122** — better than either fitted
exponent managed (0.192 scalar, 0.205 per-band) and with no fitted number left to disagree
about. Cost: **1.70% → 1.76% of one core.**

### What is verified, and what is not

**The Phase 2 gate is 3 of 4, and the fourth is LEVELS.** Not tagged complete.

| item | |
|---|---|
| flux | **closed** — continuous and independent, confirmed on the real-audio path |
| onset | **closed** — fires at the rate the material has, confirmed on real captures |
| parity | **closed** — mode A green on all three timelines |
| **levels** | **open** — three real fits disagree by up to 2.37×, so no coefficients are committed |

Tempo moved off this list rather than closing on it. The real captures showed the octave
problem was largely an artefact of `MusicLike`, and that the one thing that actually mattered —
being *confidently* wrong — has not reproduced on real material: the single wrong reading came
with confidence 0.047 and the §7.3 fallback carried it. The tracker is conservative rather than
correct, which is the behaviour it was designed for. What is left is narrower and is recorded
under "Still open": no capture yet shows a confident lock on Dead material, so `tempoLock` stays
provisional.

Levels is the honest blocker. It is one measurement away from closing and that measurement is
more captures, which now accumulate on their own.

199 tests green, 1 skipped and documented. Tempo lock at 60/90/120/132/150/180 BPM; a click
track drifting ±8% over 60 s tracked within 2 BPM after converging in 0.94 s; the
`bpmConfidence < 0.35` fallback engaging on 100% of frames of arrhythmic material with beat
phase anti-correlating at r = −1.000 with the onset envelope; key detection on five triads;
band isolation at three sample rates; a real capture replaying in the harness; the rhythm
layer's pulse shaping, confidence gating, envelope clamping and geometry-not-luminance
constraint each asserted rather than described; and §9.2's parsing against a fixture list of
real Spotify strings, including the ones that must produce **no** date.

**Standalone build**, verified: the publish folder copied outside the repo and run with a bare
`C:\Windows\system32` PATH and no `DOTNET_ROOT` comes up fully — GL, audio, hotkeys, config,
auto-capture and SMTC all start. One exe (197 MB), `glfw3.dll`, and loose `shaders/`,
`presets/`, `data/`.

**Verified against live Spotify.** SMTC attached to a real session and resolved three tracks
end to end — see "What the first real captures said" for the data:

| gate item | |
|---|---|
| SMTC attaches to a live Spotify session | **pass** — filter, events and poll all exercised |
| auto-capture produces ≥3 real fixtures | **pass** — four 60 s captures, unattended |
| tier 1, dated live release | **pass, twice** — `sugar magnolia` over `era-brent` from 6/30/85, and over `era-europe72` from 8/27/72 |
| tier 3, non-Dead | **pass, twice** — an Eagles remaster and a Vince Gill record both refused by the artist gate |
| caption shows show date and venue | **pass** — `Sugar Magnolia · Grateful Dead · Columbia, MD · 6/30/85` |
| tier 2, Dead studio album with no date | **not seen** — nothing without a date has played |

The two tier-1 passes are worth separating: the same song resolved to **two different eras**,
2 and 4, purely from the dates in its album titles. That is the era map doing the job it exists
for, on material nobody selected for it.

The caption is worth a note too. It first rendered as date-only, because 1985-06-30 was not in
`data/venues.json` — which is the degradation behaving, not a bug: an unknown show is supposed
to lose the venue and keep the date. Adding the row it had just told us about produced the full
§8.5 line. That row came from a capture, which is the data file doing what §9.2 says it is for.
The 1972 capture needed no such help — 8/27/72 was already in the book, because it is the show
§8.5 uses as its own example.

**Still needs a human.** Two things:

- **Tier 2** — a Dead studio album, something off *American Beauty* or *Workingman's Dead*. It
  is the one path with no real observation behind it;
- **the caption on screen.** Its text is asserted and its render path runs, but nobody has
  looked at it. Note that `S` screenshots will not show it: the screenshot reads the composite
  target while the caption is drawn afterwards into the back buffer, deliberately, so that
  chrome can never enter the luminance §12's limiter measures. That is the right trade and it
  means the caption has to be verified by eye.

Metadata-unavailable is verified: the app runs fully with `metadata.enabled: false`, and the
null source is the same code path a failed SMTC load takes.

**Cross-host parity, mode A** — 2026-09-01, after the flux, octave, onset, composite and
motion-sync changes, across all three committed timelines:

| Timeline | Frames | Mean | Min | |
|---|---|---|---|---|
| `timeline-20s` | 780 | 1.0000 | 0.9998 | pass |
| `timeline-novelty` | 780 | 1.0000 | 0.9982 | pass |
| `timeline-real-20s` | 1222 | 0.9999 | 0.9977 | pass |

`timeline-real-20s` was **regenerated** after the onset fix, because its F_ONSET column was
recorded by the old detector and no longer represented what the DSP produces. Its min is the
lowest of the three at 0.9977, which is expected: it is the only timeline whose features are
real rather than authored, so it drives the mapping ranges hardest.

**Budget gate**, 60 s scripted timeline, Release, GL timer queries:

| Assertion | Measured | Budget | |
|---|---|---|---|
| p99 frame time | 5.11 ms | 16.6 ms | pass |
| Gen0 collections in window | 0 over 15 920 frames | 0 | pass |
| Private bytes | 91.9 MB | 128 MB | pass |
| Private-bytes growth in window | 13.4 MB | 24 MB | pass |

(p50 2.89 ms, max 28.86 ms. The max is a single outlier, which is why the gate grades a p99
and not a maximum. Working set, reported but ungraded: 154.9 MB against a 137.3 MB pre-loop
floor.)

The ceiling is graded on private bytes rather than working set, and the plan's 60 MB figure
was retired against measurement — see "Where §11's footprint budget departs from the plan".
Managed heap is 1.8 MB: the renderer is not what spends the footprint.

**DSP cost with the render loop competing for the core** (Release, live WASAPI loopback on
dense material): 0.182 ms/hop, **1.70% of one core**, 3184 hops, 0 samples dropped. Phase 2
reported 1.82% as-scheduled, so adding a render loop did not make it worse.

#### Closed

**F_FLUX is §7.2's independent continuous drive.** It measured p50 0.000 / p90 1.000 — two
spikes and nothing between — on the real-audio fixture as well as on synthetic input, so it
was never a WAV artifact. Two structural causes: the divisor was the onset comparator's own
threshold, so the field saturated on essentially every onset and duplicated `F_ONSET`; and
raw flux is half-wave rectified, so it is ~0 wherever the spectrum falls (42% of frames on
dense material, 91% on a click track).

Fixed by smoothing, then normalizing against the observed range with the clamp applied
*after* the affine map, and by tracking the adaptive window on the envelope rather than on the
raw extremes. Confirmed on the **real-audio path**, not only synthetically — the regenerated
capture reads p10 0.013 / p50 0.135 / p90 0.536 where it used to read 0.000 / 0.000 / 1.000.

**F_ONSET fires at the rate the material has.** The 50 ms refractory is four times shorter
than the 150 ms envelope decay, so a detection on the shoulder of a peak re-armed the impulse
before it had fallen anywhere:

| | before | after | material has |
|---|---|---|---|
| onsets/s, dense (108 BPM) | 13.5 | **4.1** | ~3.6 |
| onsets/s, dense (80 BPM) | 14.3 | **3.4** | ~2.7 |
| onsets/s, click track | 2.9 | 2.2 | 2.0 |
| `F_ONSET` mean | 0.762 | **0.477** | |
| bolt gate (`onset > 0.82`) armed | 40.4% of frames | **11.9%** | |

Sparse material was always correct, which is exactly why the earlier falling-edge guard looked
sufficient. Confirmed on the real-audio path as well: the regenerated capture reads onset
p50 0.457 / mean 0.497 against 0.867 / 0.840 before.

The fix is a post-onset bar at **3.5× the firing peak**, decaying at 130 ms. A multiplier
*below* 1 cannot work — if the bar starts under the peak that armed it, an equal peak one hop
later already clears it, and 0.72 was measured to take 13.5/s only down to 10.6/s. Above 1,
the minimum spacing between equal peaks becomes `tau·ln(mult)` = 163 ms, while a genuinely
stronger hit still breaks through sooner.

#### Still open

**No real captures exist.** `tests/fixtures/real/` contains its README and nothing else. The
five the plan designates — `tight-backbeat`, `drifting-jam`, `free-time`, `quiet-dynamic`,
`modern-loud` — have never been recorded, and four things are blocked on them.

**0. The captures work, and they have started answering things.** Three arrived from ordinary
listening and are analysed above. The tooling to keep going:

```powershell
node tools/timeline-stats.mjs tests\fixtures\real\auto\*.json    # is the take usable
node tools/acf-octave.mjs     tests\fixtures\real\auto\*.json    # octave structure per capture
node tools/fit-band-gamma.mjs tests\fixtures\real\auto\*.json    # per-band fit + cross-capture spread
```

`acf-octave` has already earned its keep: it showed the beat is present at 78.5% of the
subdivision on Dead material against under 12% on `MusicLike`, which is what retired the
"unfixable" reading of the octave problem. `fit-band-gamma` exits nonzero until the captures
agree, so it can be run without judgement — it will say when there is enough.

What would help most now: **more Dead material specifically**, and at least one studio album
with no date, which is the only tier with no real observation behind it.

**1. Tempo octave on dense material — and the Phase 4 "fix" was not real.**

**Substantially answered by the real captures — read "What the first real captures said" first.**
The short version: `MusicLike`'s missing beat was a property of the fixture, real drums invert
it, and the confidently-wrong failure mode has not reproduced on real material. What remains is
narrower than what this section describes: no capture yet shows the tracker locking
*confidently* on Dead material, which is the condition `tempoLock` needs. The history below is
kept because it is why `CombDoublingEnabled` and the tempo prior are both off, and why turning
either on needs evidence rather than an argument.

Fixing `F_ONSET` exposed this. The comb discriminator was working *because* the onset envelope
was saturated: a detector firing 13.5 times a second produced a dense envelope whose
interleaved combs had plenty of content to compare. A correct detector fires 4.1 times a
second, and the on-beat and off-beat spectral rises then come out within 2% of each other.

| | before onset fix | after |
|---|---|---|
| MusicLike 88 BPM | 85.00 @ conf 0.86 | **176.67 @ conf 0.88** |
| MusicLike 80 BPM | 85.24 @ conf 0.49 | **158.42 @ conf 0.89** |
| MusicLike 108 BPM (control) | 107.70 @ conf 0.08 | 108.17 @ conf 0.47 |
| click 60/90/120/150/180 | all lock | all lock, unchanged |

Both dense cases now report the subdivision *and are confident about it*, which is the state
that matters: a wrong octave below the 0.35 floor costs nothing, because nothing downstream
uses the grid.

`CombDoublingEnabled` is therefore **false**. The instrument still runs every hop and
`CombRatio` is still exposed, but it no longer moves the octave. Leaving it active was worse
than useless — it fired spuriously mid-run on §14's drifting-click-track case, a **64.3 BPM
excursion at 52.4 s** against a ±2 BPM requirement, on material that has been correct for the
whole project. Fixed-tempo click tracks were unaffected, which is why only the drift test
surfaced it.

Three re-calibrations were tried and measured, and all were worse. Weighting onset strength by
the kick band (20–150 Hz) — the beat is where the kick is — broke tempi that had never been
wrong, because a click track's lowpassed noise bursts vary randomly in the low band and the
comb read that as alternation: 120 BPM halved to 60.00, 150 BPM to 74.01. Raising the gate
multiplier moved the onset rate without moving the comb at all. And the 80-versus-88 split
does not look like a property of the DSP: comb 1.57 versus 1.00 on the same construction,
which points at where `MusicLike`'s half-bar chord strikes and four-bar crash happen to land
inside a 4 s window.

That is the argument for stopping rather than iterating. This is the third time a number
fitted against `MusicLike` has not meant what it appeared to, and the plan already names the
instrument that settles it: `tight-backbeat`, unambiguous by construction.

**2. `LevelGamma` is closed — by deleting it, not by fitting it.**

The refit was never going to land. Three real fits disagreed by up to 2.37×, and `air` by 30×,
for a reason that is structural rather than statistical: a 1972 board tape has no content above
10 kHz, so its `air` median is exactly 0.0000, and there is no exponent that maps zero onto a
target. `LevelGamma`, `BandGamma` and `Calibrate` are gone; §7.1 now normalizes each field by
its **percentile within its own 45 s history**, with §7.1's 6 dB floor carried forward as a
spread gate so a flat band reads a stable zero instead of expanding its own noise. The
measurement, the numbers and the retained rationale are in the deviation table and the note
under it; `PercentileNorm`'s class comment carries the same record next to the code, including
the bin-range bug that made the whole spectrum read zero on loud material.

**Validated on real material, 2026-09-03.** The blocker was that the four committed captures
were post-DSP feature timelines — they hold the old normalizer's output, so no new normalizer
can be replayed through them. Five fresh captures under the current DSP resolved it:

- **1966 Fillmore, 7/3/66** — Cream Puff War, Viola Lee Blues, Cold Rain and Snow. An early
  desk feed: the air band reads 0.000 on two of the three.
- **American Beauty, 2013 remaster** — Box of Rain, Friend of the Devil. A modern full-range
  master.

That is a wider spectral gap than the 1972-vs-1985 pair originally planned for.

**The test is not "do the eras agree exactly".** Two different songs have genuinely different
spectral balance, and a normalizer that flattened that out would be destroying the signal it
exists to deliver. The test `tools/cross-era.mjs` applies is whether the disagreement BETWEEN
eras is smaller than the disagreement WITHIN one era — if it is, the residual variation is
musical rather than technological and the recording medium has stopped leaking into the
visuals. **19 of 20 measured fields pass**, including all eight bands and all four level
fields:

```
field          1966-live  2013-studio   between    within   verdict
rms                0.483        0.447     0.036     0.114   musical
peak               0.454        0.452     0.001     0.149   musical
loudSlow           0.509        0.476     0.034     0.111   musical
dynamics           0.365        0.333     0.033     0.074   musical
sub                0.595        0.484     0.110     0.176   musical
bass               0.587        0.596     0.008     0.211   musical
lowMid             0.476        0.445     0.030     0.225   musical
mid                0.493        0.452     0.041     0.288   musical
hiMid              0.455        0.434     0.021     0.225   musical
presence           0.449        0.423     0.026     0.198   musical
brilliance         0.414        0.351     0.063     0.285   musical
air                0.013        0.181     0.168     0.306   musical
flux               0.259        0.260     0.001     0.037   musical
onset              0.469        0.457     0.011     0.034   musical
percussivity       0.323        0.325     0.002     0.036   musical
keyConf            0.339        0.337     0.001     0.061   musical
chromaFlux         0.542        0.586     0.044     0.045   musical
centroid           0.548        0.468     0.080     0.110   musical
stereoWidth        0.492        0.322     0.170     0.033   MEDIUM SHOWS THROUGH
novelty            0.138        0.199     0.061     0.127   musical
```

`bass` differs by **0.008** between a 1966 desk feed and a 2013 remaster, against 0.211 between
songs. `peak` by 0.001, `flux` by 0.001, `keyConf` by 0.001, `percussivity` by 0.002. And
**`air`, the field that killed the exponent, passes**: it reads 0.000 on the two 1966 captures
that have nothing above 10 kHz, and the spread gate reports that as a stable zero instead of
demanding the 30× disagreeing correction the fit wanted. The band with no content is handled
by construction now.

The single failure is **`stereoWidth`, which is not normalized at all** — it is the raw
geometric ratio `rmsSide / (rmsMid + rmsSide)`, outside §7.1's scope entirely. A 1966 Fillmore
stereo mix really is wider than a 2013 studio master, and 0.492 against 0.322 is that fact,
correctly reported. It is not a defect and should not be "fixed".

**A caveat on the method, because it changed the answer once.** With two captures per era,
`centroid` flagged (between 0.110 against within 0.051) and it was tempting to read that as
corroboration, since `centroid` is the only normalizable field still on the OLD window
`Normalizer` rather than a `PercentileNorm`. Adding a third 1966 capture moved it to a pass
(0.080 against 0.110) — because with n = 2 the within-era spread is a single pairwise
difference and badly underestimates the real variation. **Two per era is the minimum for the
comparison to mean anything, and it is not enough to convict a field on.** `centroid` remains
the weakest pass in the table and moving it to `PercentileNorm` is still the right change; it
just is not yet evidence of a defect. That change is not a drop-in either: `PercentileNorm`'s
bins are 1 dB and its floor is 6 dB, while centroid's input is in octaves, so the bin width
and spread floor have to become constructor parameters first.

`tools/fit-band-gamma.mjs` is deleted and `timeline-stats --gamma` is gone with it. Both
inverted a curve that no longer exists and would have reported fiction about every capture
recorded after the change.

A **1972 Europe '72** capture (Mr. Charlie, Frankfurt 4/26/72) has since arrived. One more
track from that run makes a third era, and 1972 is the interesting middle case: a board tape
with real high-frequency content but not a modern master.

**3. Preset retuning — not done, but no longer blocked.** All 13 presets were authored against
the synthetic driver's ranges and none has been touched. Item 2 was the blocker and it has
lifted: retuning against real material only makes sense once real material reads the same
whatever tape it came off, and as of the cross-era measurement it does.

What is left to close, real 2013-studio medians against the synthetic driver the presets were
authored on:

```
band          synthetic   real    delta
sub               0.307  0.484   +0.177
bass              0.457  0.596   +0.139
lowMid            0.393  0.445   +0.052
mid               0.373  0.452   +0.079
hiMid             0.344  0.434   +0.090
presence          0.227  0.423   +0.196
brilliance        0.267  0.351   +0.084
air               0.212  0.181   -0.031
```

Modest against the +0.04 to +0.41 the scalar produced, and concentrated in `sub`, `bass` and
`presence` rather than spread across the spectrum. More importantly the shift is now
*consistent*: a retune done against these numbers will still be right on the next recording,
which was never true before — the previous deltas depended on which tape was playing, so any
retune would have been fitted to one capture. The feature recorder and harness replay are
ready for it.

**4. The 8-hour soak has not completed.** Started twice; the machine went idle both times. The
longest clean run is **30 minutes**, and it is encouraging rather than conclusive: private
bytes stepped 78.0 → 89.4 MB at 5 min and → 116.8 MB at 15 min, then sat at 116.9 / 117.0 /
117.2 over the next three reports. Handles flat at ~502. **Gen0 = 1 for the whole run.** That
step pattern is the GL driver allocating lazily, and it is why the drift verdict is graded
from a baseline latched at 20 minutes rather than from process start.

**5. The tempo prior does not rescue the octave, and the measurement says why.** A prior over
lag is not a function of the ACF, so unlike a harmonic sum it *can* move the choice —
`Analyzer.PriorSigma` implements `acf[lag] · P(lag)` with `P` log-normal in BPM around 120, and
`GateDiagnostics.Tempo_prior_sweep` sweeps it. Committed at sigma 0, i.e. off:

| sigma | click 60 | click 90 | click 150 | click 180 | ML 80 | ML 88 |
|---|---|---|---|---|---|---|
| off | 60.0 | 90.2 | 148.8 | 180.0 | 158.4 ✗ | 176.7 ✗ |
| 0.15 | 70.3 | 97.0 | 144.2 | 180.0 | 152.0 ✗ | 176.9 ✗ |
| 0.12 | 71.2 | 100.4 | 140.6 | **93.8 ✗** | 148.0 ✗ | 180.0 |
| 0.10 | 72.1 | 104.2 | 137.2 | 95.3 ✗ | 144.2 ✗ | **93.8 ✓** |

It **breaks the 180 BPM click track at sigma 0.12, before it fixes 88 BPM at 0.10** — and
metronomic accuracy is already degrading well above that (60 BPM reads 70.3 at sigma 0.15).
Working backwards from the flip point gives the useful number: the prior needs a 12.6× ratio
to win, so `acf[beat]` is under **12% of `acf[subdivision]`** on `MusicLike`. The beat is very
nearly absent from that fixture's onset envelope, which is a property of the fixture rather
than of the tracker — real material with a kick on the beat should have a strong peak there.
That is the same conclusion the comb reached from the other direction, and the same instrument
settles both: record `tight-backbeat` and re-run the sweep alongside the comb re-fit.

## Non-negotiables

Carried from the build plan. These are gates, not aspirations.

1. **Zero heap allocation in the render loop.** Enforced by a zero-Gen0-collection assertion in
   the Phase 4 budget test. This is what catches an accidental LINQ expression in a hot path.
2. **The safety limiter applies unconditionally** and is not preset-overridable below its floor.
   This runs where anyone can walk past it. See build plan §12.
3. **Three-tier metadata degradation.** Song mode → era → audio-only, each independently good.
   The app must run fully with no metadata at all — metadata failure never takes down rendering.
4. **No Spotify Web API dependency.** See build plan §2. Metadata comes from Windows SMTC.
