# Test fixtures

## `timeline-20s.json`

1200 rows (20 s at 60 fps) of the 56-float Feature Frame, recorded off the harness's synthetic
driver — the input side of the Phase 1 cross-host parity gate.

**Committed rather than regenerated, because recording is not reproducible.** The synthetic
driver sprinkles onsets and chord changes with `Math.random()`, so every recording is a fresh
draw. Replay, which is the part the gate depends on, *is* deterministic: one row per frame,
uploaded verbatim, `dt` pinned to `1/rate`.

Re-record only deliberately, and re-baseline the gate when you do:

```powershell
node tools/parity-harness.mjs --record tests/fixtures/timeline-20s.json --seconds 20 --rate 60
```

What this recording covers: onsets to 0.86, beat phase sweeping at 112 BPM, bass 0.15–0.75,
chord changes to 0.88. What it does not: `novelty` never fires (see README's "Known coverage
gap"), so the `novelty` mappings are untested by the gate.
