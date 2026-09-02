#!/usr/bin/env node
/*
 * tools/acf-octave.mjs
 *
 * Autocorrelate a captured onset envelope and report the OCTAVE STRUCTURE: how strong the
 * correlation is at the reported beat period, at half it, and at twice it.
 *
 * This exists to answer one question with real material that could not be answered with
 * synthetic. On `MusicLike`, `acf[beat]` measured under 12% of `acf[subdivision]` — the beat
 * was nearly absent from the onset envelope — and that is why every attempt to fix the octave
 * failed: no reweighting recovers a peak that is not there. The hypothesis was that real drums
 * would invert it, because a kick on the beat puts a large broadband transient exactly where
 * MusicLike had only hats.
 *
 * It reads the `onset` column out of a recorded timeline, so it needs no audio and reproduces
 * the analyzer's own view: `Analyzer.Tempo` autocorrelates precisely this signal.
 *
 * Usage:
 *   node tools/acf-octave.mjs <timeline.json> [more.json ...] [--bpm N]
 *
 *   --bpm N   the tempo you believe the music actually is, if the tracker's reading is
 *             suspect. Without it, the file's own median BPM is used.
 */
import { readFileSync } from 'node:fs';
import { basename } from 'node:path';

const argv = process.argv.slice(2);
const files = argv.filter(a => !a.startsWith('--') && !/^\d+(\.\d+)?$/.test(a));
const bpmIdx = argv.indexOf('--bpm');
const forcedBpm = bpmIdx === -1 ? null : Number(argv[bpmIdx + 1]);

if (files.length === 0) {
  console.error('usage: node tools/acf-octave.mjs <timeline.json> [...] [--bpm N]');
  process.exit(2);
}

/* Same window the analyzer uses: TempoHistorySeconds = 4 s of onset envelope, mean removed
   and normalized by variance, so each lag is a Pearson coefficient rather than a raw product. */
const WINDOW_SECONDS = 4.0;
const MIN_BPM = 60, MAX_BPM = 180;

function column(tl, name) {
  const i = tl.fieldOrder.indexOf(name);
  if (i < 0) throw new Error(`no '${name}' column in ${tl.kind ?? 'timeline'}`);
  const rows = tl.rows ?? tl.data ?? tl.frames;
  return rows.map(r => r[i]);
}

const median = (v) => { const s = [...v].sort((a, b) => a - b); return s[Math.floor(s.length / 2)]; };

/* Mean-removed, variance-normalized autocorrelation over one window. */
function acfWindow(env, lagMax) {
  const n = env.length;
  const mean = env.reduce((a, b) => a + b, 0) / n;
  let varr = 0;
  for (const v of env) varr += (v - mean) * (v - mean);
  varr /= n;
  if (varr < 1e-12) return null;

  const acf = new Float64Array(lagMax + 1);
  for (let lag = 1; lag <= lagMax; lag++) {
    let sum = 0;
    const taps = n - lag;
    for (let i = 0; i < taps; i++) sum += (env[i] - mean) * (env[i + lag] - mean);
    acf[lag] = sum / taps / varr;
  }
  return acf;
}

for (const file of files) {
  const tl = JSON.parse(readFileSync(file, 'utf8'));
  const rate = tl.rate ?? 93.75;
  const onset = column(tl, 'onset');
  const bpmCol = column(tl, 'bpm');
  const confCol = column(tl, 'bpmConf');

  const locked = bpmCol.filter((_, i) => confCol[i] >= 0.35);
  const reportedBpm = locked.length ? median(locked) : median(bpmCol);
  const bpm = forcedBpm ?? reportedBpm;

  const win = Math.round(WINDOW_SECONDS * rate);
  const lagMax = Math.min(win >> 1, Math.ceil(rate * 60 / MIN_BPM));
  const lagMin = Math.max(2, Math.floor(rate * 60 / MAX_BPM));

  // Average the ACF over every window in the capture, so the answer is a property of the
  // whole minute rather than of whichever four seconds happened to be sampled.
  const sum = new Float64Array(lagMax + 1);
  let windows = 0;
  for (let start = 0; start + win <= onset.length; start += Math.round(rate)) {
    const a = acfWindow(onset.slice(start, start + win), lagMax);
    if (!a) continue;
    for (let l = 0; l <= lagMax; l++) sum[l] += a[l];
    windows++;
  }
  if (!windows) { console.log(`  ${basename(file)}: no usable window`); continue; }
  for (let l = 0; l <= lagMax; l++) sum[l] /= windows;

  const lagFor = (b) => Math.round(rate * 60 / b);
  const at = (lag) => (lag >= 1 && lag <= lagMax ? sum[lag] : null);

  // The global peak inside the search range — what the tracker would pick unaided.
  let peakLag = lagMin, peak = -Infinity;
  for (let l = lagMin; l <= lagMax; l++) if (sum[l] > peak) { peak = sum[l]; peakLag = l; }

  const beatLag = lagFor(bpm);
  const halfLag = lagFor(bpm * 2);      // the SUBDIVISION: half the period
  const dblLag = lagFor(bpm / 2);       // twice the period

  const beat = at(beatLag), sub = at(halfLag), dbl = at(dblLag);

  console.log('');
  console.log(`  ${basename(file)}`);
  console.log(`  ${'-'.repeat(Math.min(78, basename(file).length))}`);
  if (tl.label) console.log(`  ${tl.label}`);
  console.log(`  ${onset.length} frames @ ${rate} fps, ${windows} windows averaged`);
  console.log(`  reported BPM ${reportedBpm.toFixed(1)} (locked on `
    + `${(100 * locked.length / bpmCol.length).toFixed(1)}% of frames)`
    + (forcedBpm ? `, analysing against ${bpm.toFixed(1)}` : ''));
  console.log('');
  console.log(`  ${'position'.padEnd(22)} ${'lag'.padStart(5)} ${'BPM'.padStart(7)} ${'acf'.padStart(8)}`);
  console.log('  ' + '-'.repeat(46));
  const row = (name, lag, v) => console.log(
    `  ${name.padEnd(22)} ${String(lag).padStart(5)} `
    + `${(rate * 60 / lag).toFixed(1).padStart(7)} ${(v === null ? '  n/a' : v.toFixed(4)).padStart(8)}`);

  row('global peak', peakLag, peak);
  if (dbl !== null) row('2x the period', dblLag, dbl);
  if (beat !== null) row('the beat', beatLag, beat);
  if (sub !== null) row('the subdivision', halfLag, sub);

  console.log('');
  if (beat !== null && sub !== null) {
    if (sub <= 0) {
      // The STRONGEST case, and worth naming rather than skipping: a negative correlation at
      // half the period means the envelope alternates strong/weak, which is a backbeat. The
      // subdivision is not merely weaker than the beat, it is evidence against itself.
      console.log(`  acf[subdivision] = ${sub.toFixed(4)} — NEGATIVE.`);
      console.log('  -> the envelope alternates at half the period, which is a backbeat. The');
      console.log('     octave is not ambiguous here at all; nothing could mistake this one.');
    } else {
      const ratio = beat / sub;
      console.log(`  acf[beat] / acf[subdivision] = ${ratio.toFixed(3)}`);
      console.log(ratio >= 1
        ? '  -> the BEAT is the stronger periodicity. A prior or a comb has something to work'
        + ' with;\n     this is the case MusicLike could not produce.'
        : ratio >= 0.12
          ? '  -> the subdivision is stronger, but the beat is PRESENT. Recoverable in'
          + ' principle —\n     unlike MusicLike, where it was not there to recover.'
          : '  -> the beat is nearly ABSENT (under 12%), the same shape MusicLike had.'
          + '\n     No reweighting of the ACF can recover a peak that is not there.');
    }
    console.log(`  peak strength ${peak.toFixed(4)} — `
      + (peak > 0.25 ? 'a clean grid' : peak > 0.12 ? 'a usable grid' : 'a weak grid'));
  }
}
