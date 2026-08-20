# Real-audio captures

Feature Frame timelines captured from live WASAPI loopback by the Phase 2 DSP, one row per
analysis hop (93.75 rows/s at 48 kHz).

Committed, unlike `captures/`, because they are small JSON and their value is as a
**regression baseline**. A DSP change can be re-measured against the same material instead
of against a fresh recording of something slightly different, which is the difference
between a regression test and a one-time measurement.

## Capturing

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

## What each capture is for

Five captures span the space that actually matters for this DSP. The point of the set is
that the **right answer differs per capture** — a tracker that locks on everything is as
wrong as one that locks on nothing.

| Capture | Material | What it must show |
|---|---|---|
| `tight-backbeat` | Studio Dead, rock-solid tempo | Locks, high confidence, near-zero fallback |
| `drifting-jam` | Long live jam with real drift | Locks, and the BPM median MOVES across the run |
| `free-time` | Drums → Space | Fallback on nearly every frame — this is correct, not a failure |
| `quiet-dynamic` | Sparse low-level passage | Bands still use their range; adaptive normalization earning its keep |
| `modern-loud` | Non-Dead, heavily compressed | Generalization: does the calibration hold outside the corpus |

`bpmConfidence < 0.35` is §7.3's fallback threshold. "Fraction of frames below 0.35" is not
a quality score — it is the answer to "did this passage read as pulsed or as free time",
and Drums → Space should be ~100%.
