# Real-audio captures

Feature Frame timelines captured from live WASAPI loopback by the Phase 2 DSP, one row per
analysis hop (93.75 rows/s at 48 kHz).

Committed, unlike `captures/`, because they are small JSON and their value is as a
**regression baseline**. A DSP change can be re-measured against the same material instead
of against a fresh recording of something slightly different, which is the difference
between a regression test and a one-time measurement.

## Almost everything here is in `auto/`

Eleven captures exist and the app wrote all eleven by itself: when SMTC reports a new track
and playback is active, it records 60 s and names the file from the metadata. Nobody decides
to make one. **`auto/README.md` is the file to read** — it lists what is there, which DSP
generation each capture belongs to, and how to run the analysis tools over them.

That folder is the answer to why this one sat empty for four development cycles. The captures
the plan designates were each a separate deliberate act, so none of them ever happened, and
the tempo octave and the per-band gamma stayed open for want of material that ordinary
listening produces by the dozen.

## Capturing on purpose, which is still sometimes right

Auto-capture names a capture after whatever was playing. It cannot produce a capture named
after *material* — and one of those is still an open dependency:
`OctaveAndFluxTests.Tempo_octave_on_dense_material_is_open` is skipped until
`tight-backbeat.json` exists in this directory, because re-fitting `CombAlternation` needs a
recording that is unambiguous by construction rather than one that happened to come up.

`scripts/capture.ps1` writes here, at the top level, not into `auto/`:

```powershell
.\build.ps1 -Configuration Release          # Debug reports DSP cost ~2.5x high
.\scripts\capture.ps1 <name> "<label>" [-Seconds 60]
```

The wrapper exists because Ripple is a `WinExe` and PowerShell does not wait for one —
calling the exe directly returns immediately and the file appears later, or not at all.

Then check the take before moving on:

```powershell
node tools/timeline-stats.mjs tests\fixtures\real\<name>.json
```

## The five names, and what they are for

The plan designates five, and none has been recorded under its own name. They are worth
keeping as a description of the **space** a capture set has to span, because the point of the
set is that the right answer differs per capture — a tracker that locks on everything is as
wrong as one that locks on nothing.

| Capture | Material | What it must show |
|---|---|---|
| `tight-backbeat` | Studio Dead, rock-solid tempo | Locks, high confidence, near-zero fallback |
| `drifting-jam` | Long live jam with real drift | Locks, and the BPM median MOVES across the run |
| `free-time` | Drums → Space | Fallback on nearly every frame — this is correct, not a failure |
| `quiet-dynamic` | Sparse low-level passage | Bands still use their range; adaptive normalization earning its keep |
| `modern-loud` | Non-Dead, heavily compressed | Generalization: does the calibration hold outside the corpus |

`auto/` covers one of these by accident rather than by name: the Little Feat capture is
non-Dead and so does `modern-loud`'s job — generalization outside the corpus — though it is a
1977 live album rather than a compressed modern master.

The other four are not covered, and two of them cannot be. **`drifting-jam` is structurally out
of reach of auto-capture**: it records 60 s, and 60 s of a jam does not drift. Measured on the
two longest-form auto captures, `viola-lee-blues` moves +0.9 BPM first third to last and
`fat-man-in-the-bathtub` −1.0 BPM, which is a stable tempo sampled briefly, not drift. Showing
a BPM median that moves needs a deliberate capture over several minutes. `tight-backbeat` is
the same problem from the other side — it needs material chosen for being unambiguous, which is
not a property of whatever came up next.

`bpmConfidence < 0.35` is §7.3's fallback threshold. "Fraction of frames below 0.35" is not
a quality score — it is the answer to "did this passage read as pulsed or as free time",
and Drums → Space should be ~100%.
