#!/usr/bin/env node
/*
 * tools/rhythm-depth.mjs
 *
 * Which rhythmic mapping routes actually modulate their target, and how strongly?
 *
 * This exists because "does it look beat-synced" is not a measurement, and the four routes
 * added in the motion-sync pass were not equally successful — two worked, two did nothing,
 * and the difference was invisible by eye but obvious in the numbers. It correlates each
 * traced draw-state scalar against the shaped drive that is supposed to be moving it.
 *
 * Reads a `--trace` dump (per-frame draw scalars, written by the native host) and the
 * timeline that produced it (the feature values).
 *
 * Usage:
 *   node tools/rhythm-depth.mjs <trace.json> <timeline.json>
 *
 * Two numbers per route:
 *   depth  the swing of the parameter ABOVE ITS OWN BASELINE, as a fraction of its mean
 *          excursion. Measured against the baseline and not the absolute value, because
 *          zoom sits at ~1.0 and only its excess above 1.0 drives anything — dividing by
 *          the absolute mean reported 0.8% for a swing that actually doubled the effect.
 *   corr   correlation with the shaped drive. Near 0 means the route is wired but swamped
 *          by something else already writing that parameter, which is how the
 *          barPhase -> paletteRotate route was caught: r = -0.028 because keyHue
 *          accumulates palRot into the hundreds and a pulse of ~1.0 per bar is noise.
 */
import { readFileSync } from 'node:fs';

const tr = JSON.parse(readFileSync(process.argv[2], 'utf8'));
const tl = JSON.parse(readFileSync(process.argv[3], 'utf8'));
const keys = tr.keys, rows = tr.rows;
const ord = tl.fieldOrder, tlRows = tl.rows ?? tl.data ?? tl.frames;

const fi = (n) => ord.indexOf(n);
const N = Math.min(rows.length, tlRows.length);

const beatPhase = [], barPhase = [], onset = [], flux = [], conf = [];
for (let i = 0; i < N; i++) {
  beatPhase.push(tlRows[i][fi('beatPhase')]);
  barPhase.push(tlRows[i][fi('barPhase')]);
  onset.push(tlRows[i][fi('onset')]);
  flux.push(tlRows[i][fi('flux')]);
  conf.push(tlRows[i][fi('bpmConf')]);
}

/* Same shaping the hosts apply — see RhythmicShape() and rhythmicShape(). */
const beatPulse = beatPhase.map((v, i) => Math.pow(1 - v, 3.0) * conf[i]);
const barPulse = barPhase.map((v, i) => Math.pow(1 - v, 2.0) * conf[i]);

const corr = (a, b) => {
  const ma = a.reduce((s, x) => s + x, 0) / a.length;
  const mb = b.reduce((s, x) => s + x, 0) / b.length;
  let sab = 0, saa = 0, sbb = 0;
  for (let i = 0; i < a.length; i++) {
    const da = a[i] - ma, db = b[i] - mb;
    sab += da * db; saa += da * da; sbb += db * db;
  }
  return sab / Math.sqrt(Math.max(saa * sbb, 1e-18));
};

const col = (k) => { const i = keys.indexOf(k); return rows.slice(0, N).map(r => r[i]); };

const stats = (v, base) => {
  const s = [...v].sort((a, b) => a - b);
  const p10 = s[Math.floor(s.length * 0.10)], p90 = s[Math.floor(s.length * 0.90)];
  const mean = v.reduce((a, x) => a + x, 0) / v.length;
  const lo = p10 - base, hi = p90 - base, m = mean - base;
  return { lo, hi, depth: Math.abs(m) > 1e-9 ? (hi - lo) / Math.abs(m) : 0 };
};

/* base = where the parameter sits with no drive, so depth measures the part that acts.
   1.0 for zoom (a per-frame multiplier), 0 for the rest. */
const routes = [
  ['beatPhase -> feedbackZoomPulse', 'zoom', beatPulse, 1.0],
  ['onset     -> warpAmount', 'warpAmount', onset, 0.0],
  ['flux      -> fractalMorph', 'beatMorph', flux, 0.0],
  ['flux      -> fractalMorph', 'cells', flux, 0.0],
  /* Not wired any more — kept so the measurement that retired it can be reproduced. */
  ['barPhase  -> paletteRotate (RETIRED)', 'palRot', barPulse, 0.0],
];

console.log(`  ${'route'.padEnd(38)} ${'param'.padEnd(11)} ${'p10-base'.padStart(10)} `
  + `${'p90-base'.padStart(10)} ${'depth'.padStart(8)} ${'corr'.padStart(7)}`);
console.log('  ' + '-'.repeat(90));

for (const [name, param, drive, base] of routes) {
  const v = col(param);
  if (!v.length || v[0] === undefined) {
    console.log(`  ${name.padEnd(38)} ${param.padEnd(11)}  (not traced)`);
    continue;
  }
  const st = stats(v, base);
  console.log(`  ${name.padEnd(38)} ${param.padEnd(11)} ${st.lo.toFixed(4).padStart(10)} `
    + `${st.hi.toFixed(4).padStart(10)} ${(st.depth * 100).toFixed(0).padStart(7)}% `
    + `${corr(v, drive).toFixed(3).padStart(7)}`);
}

console.log('');
console.log(`  ${N} frames, bpmConf mean ${(conf.reduce((a, x) => a + x, 0) / N).toFixed(3)}`);
console.log('  depth = swing above the baseline / mean excursion; corr near 0 = route swamped');
