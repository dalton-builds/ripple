# Test fixtures

Timelines are the input side of the cross-host parity gate: 56-float Feature Frame rows,
one per frame, replayed verbatim with `dt` pinned to `1/rate`. Both are listed in
`tests/parity-config.json`; run them with `node tools/parity-gate.mjs --mode a --all-timelines`.

## `timeline-20s.json` — recorded

1200 rows (20 s at 60 fps) off the harness's synthetic driver, seed `0x5F356495`.

Reproducible: the driver's RNG is seeded, so re-recording at the same seed yields the same
timeline on any machine. It is still committed rather than generated on demand, because
regenerating it silently rebaselines the gate.

```powershell
node tools/parity-harness.mjs --record tests/fixtures/timeline-20s.json --seconds 20 --rate 60
```

Covers: onsets to 0.86, beat phase sweeping at 112 BPM, bass 0.15–0.75, chord changes to
0.88. Does **not** fire `novelty` — hence the second fixture.

## `timeline-novelty.json` — constructed

Built from `timeline-novelty.script.json`, not recorded. Recording cannot target a rare
event: the synthetic driver fires novelty at `dt/22` per frame, so 20 s is a coin flip.
A script fires it at frame 120 because the script says frame 120.

```powershell
node tools/make-timeline.mjs --script tests/fixtures/timeline-novelty.script.json --summary
```

Covers the two `novelty` mapping paths in the library — `feedbackDecay` (`oil-wheel`,
`dark-star-drift`) and `fractalMorph` (the other 11 presets) — with pulses at 2 s, 7 s,
12 s and 17 s peaking at 0.55 / 0.9 / 1.0 / 0.75, so both ends of the mapping range are hit.
Edit the script and regenerate; do not hand-edit the timeline.
