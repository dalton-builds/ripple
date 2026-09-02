# Auto-captured fixtures

Written by Ripple itself, unattended: when SMTC reports a new track and playback is active, the
app records 60 s of Feature Frames here, named and labelled from the metadata. Nobody decides to
make one.

That is the whole point. The five fixtures §14 depends on blocked four development cycles for a
reason that was never technical — capturing one was a separate deliberate act, so it never
happened, and the tempo octave and the per-band gamma stayed unresolvable for want of material
that ordinary listening produces by the dozen.

## Curate these

They accumulate. `metadata.autoCaptureCapMb` (200 MB default) stops the app writing more, and it
**stops rather than prunes** — a regression baseline is not something to delete automatically to
make room. But that means the folder is yours to prune.

Keep the ones that earn it:

- **material the gates need** — the five the plan names (`tight-backbeat`, `drifting-jam`,
  `free-time`, `quiet-dynamic`, `modern-loud`) are still the shape of what is wanted, whatever
  the tracks are actually called;
- **anything that showed a defect.** A capture that made the tracker read the wrong octave is
  worth more than ten that it handled;
- **spread.** Three recordings of one band in one year are one sample, not three.

Delete the rest. A capture nobody will re-measure against is 2.4 MB of noise.

## Reading them

```powershell
node tools/timeline-stats.mjs tests\fixtures\real\auto\*.json    # is the take usable
node tools/acf-octave.mjs     tests\fixtures\real\auto\*.json    # tempo octave structure
node tools/fit-band-gamma.mjs tests\fixtures\real\auto\*.json    # per-band fit + cross-capture spread
```

`fit-band-gamma` exits nonzero until the fits agree across captures, so it can be run without
judgement — it says when there is enough material to commit a calibration.

## What is here now

The first three, from ordinary listening, and the analysis they produced is in the README under
"What the first real captures said". Between them they retired the belief that the tempo octave
was unfixable, and they refused — correctly — to justify committing a level calibration.

They are committed because the analysis in this repo cites them by name. Later ones need not be.
