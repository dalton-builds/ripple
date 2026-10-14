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

# does normalization hold across recording eras — the gate for §7.1.
# It takes PATHS and does not glob, so name them in full. $D keeps the lines readable.
$D = "tests\fixtures\real\auto"
node tools/cross-era.mjs `
  --era "1966=$D\grateful-dead-cream-puff-war-live-at-fillmore-auditorium-san-francisco-ca-7-3-66-cream-puff-war.json,$D\grateful-dead-viola-lee-blues-live-at-fillmore-auditorium-san-francisco-ca-7-3-66-cream-puff-war.json,$D\grateful-dead-cold-rain-and-snow-live-at-fillmore-auditorium-san-francisco-ca-7-3-66-cream-puff.json" `
  --era "2013=$D\grateful-dead-box-of-rain-2013-remaster-american-beauty-2013-remaster.json,$D\grateful-dead-friend-of-the-devil-2013-remaster-american-beauty-2013-remaster.json"
```

Bare file names do **not** work — the paths resolve against the current directory, not against
this folder, and the tool exits with `ENOENT` rather than a usage message.

`cross-era` exits nonzero when any field's between-era gap exceeds its within-era spread, so it
can be run without judgement. **It replaced `fit-band-gamma`**, which asked each capture what
exponent would make it match the synthetic driver and got answers that disagreed by 30× — a
question with no answer, because a 1966 desk feed has no air-band content to fit. `cross-era`
asks the answerable version: is the disagreement between eras smaller than the disagreement
between two songs from the same era? If it is, the tape has stopped showing through and what is
left is music.

## Two DSP generations are in this folder, and they do not mix

Captures store the analyzer's **output**, not its input, so a capture cannot be replayed through
a changed analyzer. Anything recorded before percentile normalization landed (2026-09-03) holds
the old window-plus-exponent values and **must not be compared field by field against anything
recorded after it.** This is not a subtle trap — the level fields moved by 0.1 to 0.3.

| capture | recorded under |
|---|---|
| `sugar-magnolia-…-veneta-…-8-27-72` | pre-percentile — historical |
| `sugar-magnolia-…-merriweather-…-1985` | pre-percentile — historical |
| `eagles-take-it-easy-…` | pre-percentile — historical |
| `vince-gill-…` | pre-percentile — historical |
| `cream-puff-war-…-fillmore-…-7-3-66` | percentile |
| `viola-lee-blues-…-fillmore-…-7-3-66` | percentile |
| `cold-rain-and-snow-…-fillmore-…-7-3-66` | percentile |
| `mr-charlie-…-frankfurt-…-4-26-1972` | percentile |
| `box-of-rain-…-american-beauty` | percentile |
| `friend-of-the-devil-…-american-beauty` | percentile |
| `little-feat-fat-man-in-the-bathtub-…-lisner-…-8-10-1977` | percentile |

The historical four are kept rather than deleted because the repo README cites them by name —
the air band reading **exactly 0.0000** on the 1972 Veneta tape against 0.120 in 1985 is the
measurement that retired the fitted exponent, and deleting the evidence for a documented
decision is worse than keeping a file that needs a caveat. The two Sugar Magnolias are also the
best material in the set, the same song thirteen years apart; re-recording that pair under the
current DSP is worth doing.

## What is here now

Eleven, all from ordinary listening. The first four produced the analysis in the repo README
under "What the first real captures said": between them they retired the belief that the tempo
octave was unfixable, and they refused — correctly — to justify committing a level calibration.

The seven later ones validated what replaced it. Six of them group by era:

- **1966 Fillmore, 7/3/66** — `cream-puff-war`, `viola-lee-blues`, `cold-rain-and-snow`. An
  early desk feed with nothing above 10 kHz: the air band reads exactly 0.000 on two of them.
- **American Beauty, 2013 remaster** — `box-of-rain`, `friend-of-the-devil`. A modern full-range
  master, and also §9.4's tier 2 (the family, no parseable date) arriving on its own after two
  sessions in which it never came up.
- **Europe '72, Frankfurt 4/26/72** — `mr-charlie`. One so far, so it cannot form a group yet.
  It is the interesting middle case: a board tape with real high-frequency content but not a
  modern master. One more track from that run makes a third era.

The seventh belongs to no era group: **`little-feat-fat-man-in-the-bathtub`**, Lisner Auditorium
8/10/77. It is non-Dead, so the artist gate refuses it and it comes up as tier 3 — which makes it
the only percentile-era capture that says anything about whether the calibration holds outside
the corpus, the job the plan gave `modern-loud`. One capture is an observation, not a group.

Two recordings per era is the minimum that makes the comparison mean anything: with one each, a
difference between eras cannot be told apart from a difference between songs. **Two is also not
many.** With two per era `centroid` failed the cross-era test; a third 1966 capture moved it to
a pass, because with n = 2 the within-era spread is one pairwise difference and underestimates
the real variation. Two is enough to measure with and not enough to convict a field on — which
is why "spread" is on the keep list above, and why a third capture per era is worth recording.

They are committed because the analysis in this repo cites them by name. Later ones need not be.
