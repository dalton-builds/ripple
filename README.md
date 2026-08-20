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
- [ ] **2** — WASAPI loopback + full DSP, real audio drives visuals
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
# 1. record a 20 s timeline off the synthetic driver (committed as a fixture)
node tools/parity-harness.mjs --record tests/fixtures/timeline-20s.json --seconds 20 --rate 60

# 2. dump that timeline from both hosts - mode A, the strict gate
node tools/parity-harness.mjs --timeline tests/fixtures/timeline-20s.json `
     --out captures/modeA-harness --all --stride 20 --no-feedback --no-particles
.\run.ps1 -NoBuild --dump --no-feedback --no-particles --all `
     --timeline tests/fixtures/timeline-20s.json --out captures/modeA-native --stride 20

# 3. compare
node tools/compare-ssim.mjs captures/modeA-harness captures/modeA-native --mode a
```

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

### Known coverage gap

The committed timeline never fires a `novelty` event — the synthetic driver sprinkles those at
`dt/22` per frame and 20 s was not enough — so the `novelty -> fractalMorph` and
`novelty -> feedbackDecay` mappings are untested by this gate. Record a longer timeline, or
drive `novelty` manually, before relying on them.

## Non-negotiables

Carried from the build plan. These are gates, not aspirations.

1. **Zero heap allocation in the render loop.** Enforced by a zero-Gen0-collection assertion in
   the Phase 4 budget test. This is what catches an accidental LINQ expression in a hot path.
2. **The safety limiter applies unconditionally** and is not preset-overridable below its floor.
   This runs where anyone can walk past it. See build plan §12.
3. **Three-tier metadata degradation.** Song mode → era → audio-only, each independently good.
   The app must run fully with no metadata at all — metadata failure never takes down rendering.
4. **No Spotify Web API dependency.** See build plan §2. Metadata comes from Windows SMTC.
