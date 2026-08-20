# Frozen contracts

Three things are shared between the Claude Design harness and the native Claude Code host. Drift
between them is the primary architectural risk in this project, because the whole schedule depends
on the Phase 0 shaders running unmodified in Phase 1.

Read this before touching either side.

---

## 1. The Feature Frame UBO

**`shaders/common/features.glsl`** — frozen at `schemaVersion 1`.

Shared verbatim. Both hosts must:

- Bind the UBO at **binding point 0**.
- Upload exactly **224 bytes** = 14 × `vec4`. The breakdown, since this is easy to miscount:
  10 declarations, of which `uBands[2]` is 2 and `uChroma[3]` is 3 → 13, plus `uReserved` → 14.
- Zero-fill `uReserved`. It exists so `schemaVersion 2` can add fields without a repack.
- Use the named accessors (`F_BASS`, `F_BEAT_PHASE`, …) rather than raw swizzles, so a future
  repack touches one file.
- Normalize every field to 0..1 in the *analysis* layer, never in the shader. The fields marked
  RAW are the only exceptions.

C# side must assert:

```csharp
Debug.Assert(Marshal.SizeOf<FeatureFrame>() == 224);
```

If a field genuinely needs adding, bump `schemaVersion`, add it to the trailing `vec4`s only
(never reorder), and update both hosts in the same commit.

---

## 2. Shader dialect bridging

Shader files carry **no `#version` line**. Each host injects its own preamble at load:

| Host | Injected preamble |
|---|---|
| WebGL2 harness | `#version 300 es` + `precision highp float;` + `precision highp int;` |
| Native OpenGL | `#version 330 core` + `#define highp` + `#define mediump` + `#define lowp` |

With that, shader source is byte-identical across hosts. Both loaders must also resolve a simple
`#include "common/x.glsl"` directive — implement it as flat textual substitution with cycle
detection, no preprocessor cleverness.

**Verified at Phase 1's gate** by rendering the same scripted feature timeline in both hosts and
diffing frames. If parity fails, add a `#define RIPPLE_WEBGL` guard around the specific
divergence. **Do not fork the files.**

---

## 3. Shader extraction from the harness

Claude Design produces a single-file HTML artifact, but the shaders must land on disk as separate
files under `shaders/`. To make that extraction mechanical rather than a manual reconstruction
job, the harness must store **every shader in its own template literal, fenced with a filename
marker**:

```javascript
// ===== FILE: shaders/fractal/warp_fbm.frag =====
const SH_WARP_FBM = `
  #include "common/features.glsl"
  #include "common/noise.glsl"
  ...
`;
// ===== END FILE =====
```

The `FILE:` path is the authoritative destination. A ~20-line extraction script then splits the
HTML into the `shaders/` tree with no judgment calls. Write that script as
`tools/extract-shaders.mjs` during Phase 0 and run it as the last step of the phase, so Phase 1
starts from real files rather than from copy-paste.

**The harness must load its shaders through the same `#include` resolver as the native host.** If
the harness inlines includes by hand while the native host resolves them at load, the two will
diverge silently the first time a common file changes.

---

## 4. Preset schema

`presets/eras/*.json` and `data/songs.json`, both at `schemaVersion 1`.

Merge order, lowest precedence first:

```
era preset  →  song mode override  →  user config override  →  governor quality clamp
```

The governor clamp is **last and absolute** — a preset cannot ask for 64k particles and win
against a machine that cannot afford them.

Every key in a song mode is a *partial* override. `{"feedback": {"decay": 0.995}}` changes only
the decay and inherits the rest of the era's feedback block. Implement as a recursive dictionary
merge, not a replace.

The `mapping` block is the feature→parameter routing table. Keeping it as data is what lets new
looks come from editing JSON rather than writing shaders — treat it as load-bearing, not
decoration.

---

## What is deliberately *not* frozen

Everything visual. Palettes, feedback constants, motif gains, fractal parameters, and the era
motion characters in `presets/eras/*.json` are **starting points to be tuned in the harness on
real audio**. The numbers in there are educated guesses, not measurements. Expect to change most
of them, and change them in the JSON rather than in code.

---

## Rejected approaches

Things that were tried, measured, and abandoned. They are recorded here because each one
looks reasonable from the outside, and the reasoning that killed it is not recoverable
from the code that does not contain it.

### Compensating mode A's dimness by removing the (1-decay) paint normalization

**Symptom that motivated it.** With the feedback carry excluded (parity gate mode A), a
rendered frame is very dark: mean luma 3.7/255 with 97.8% of pixels below 4/255, against
mean 34/255 for the same preset with feedback on. SSIM computed on that is measuring 8-bit
quantization noise, not the render, and the gate scored 0.86 where it should have scored
~1.0.

**The idea.** `DrawState.Reset()` sets
`Paint = paintGain * (1 - min(decay, 0.9995)) * 30`. The `(1 - decay)` factor is there so
changing `decay` does not change exposure: the feedback buffer's equilibrium is
`paint / (1 - decay)`, so folding `(1 - decay)` into the paint cancels it. Dividing it back
out in mode A should therefore paint a single frame at exactly the amplitude the loop
would have accumulated to — a factor derived from the code rather than tuned.

**Result: worse. 0.991 -> 0.638 mean SSIM.** Do not retry it.

**Why it cannot work.** The per-frame paint is deliberately a *sparse, high-contrast
dither spray* — the harness comment at the paint pass says so outright: "The paint per
frame is deliberately SPARSE and high contrast: a dim body plus bright specks, because
the buffer reaches equilibrium at paint/(1-decay)". The image is the **integral** of that
spray. Amplifying one frame of it by ~67x does not produce a dim version of the final
image; it produces isolated blown-out specks over black. So the compensated frame is
*more* dominated by per-pixel stochastic placement than the uncompensated one, and that
placement is precisely the term that cannot match across GL implementations.

**What to do instead.** Accept that a single frame of this renderer is not a picture, and
exclude the terms that are stochastic per pixel rather than trying to scale them into
view. That is what mode A does: see `tests/parity-config.json` for the exclusion list and
the measurement behind each entry.

**The general lesson.** Dim mode-A frames are not a bug to be fixed by gain. They are
what a feedback renderer's integrand looks like. Anyone who finds those frames later and
reaches for an exposure knob should read this section first.
