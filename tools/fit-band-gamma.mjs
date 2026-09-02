#!/usr/bin/env node
/*
 * tools/fit-band-gamma.mjs
 *
 * Fit §7.1's level calibration exponents from real captures, one per band, and — the part
 * that actually decides anything — report how much the fits AGREE ACROSS CAPTURES.
 *
 * THE SPREAD IS THE RESULT, NOT THE MEAN. A single capture always yields a fit; the question
 * is whether it means anything. Two synthetic proxies disagreed by up to 4.6x (lowMid 45.59
 * against 9.89), which is what a calibration looks like when it is really measuring the
 * material rather than the DSP — and committing the mean of those would have baked one
 * recording's spectral balance into every preset. Real captures have to do better than that
 * before any number here is worth committing.
 *
 * The exponent MULTIPLIES the material difference, which is why the bar is high: at gamma 45,
 * a pre-gamma median of 0.95 instead of 0.98 moves the output from 0.40 to 0.10.
 *
 * Usage:
 *   node tools/fit-band-gamma.mjs <capture.json> [more.json ...]
 *   node tools/fit-band-gamma.mjs tests/fixtures/real/auto/*.json
 *
 *   --tolerance P   spread, as a fraction of the mean, below which the fits are called
 *                   agreeing. Default 0.20.
 */
import { readFileSync } from 'node:fs';
import { basename } from 'node:path';

/* The committed calibration exponent, from Analyzer.LevelGamma. Needed to invert the curve
   and recover what the normalizer produced BEFORE calibration. */
const COMMITTED_GAMMA = 4.66;

const BANDS = ['sub', 'bass', 'lowMid', 'mid', 'hiMid', 'presence', 'brilliance', 'air'];
const SCALARS = ['rms', 'peak', 'loudSlow'];

/* Synthetic-driver medians — the ranges all 13 presets were authored against, which is what
   the calibration exists to map real audio onto. From NormalizationTests. */
const SYNTHETIC = {
  sub: 0.307, bass: 0.457, lowMid: 0.393, mid: 0.373, hiMid: 0.344,
  presence: 0.227, brilliance: 0.267, air: 0.212,
  rms: 0.437, peak: 0.550, loudSlow: 0.420,
};

const argv = process.argv.slice(2);
const tolIdx = argv.indexOf('--tolerance');
const TOLERANCE = tolIdx === -1 ? 0.20 : Number(argv[tolIdx + 1]);
const files = argv.filter((a, i) => !a.startsWith('--') && argv[i - 1] !== '--tolerance');

if (files.length === 0) {
  console.error('usage: node tools/fit-band-gamma.mjs <capture.json> [...]');
  process.exit(2);
}

const median = (v) => { const s = [...v].sort((a, b) => a - b); return s[Math.floor(s.length / 2)]; };

function fit(file) {
  const tl = JSON.parse(readFileSync(file, 'utf8'));
  const order = tl.fieldOrder;
  const rows = tl.rows ?? tl.data ?? tl.frames;
  const col = (n) => {
    const i = order.indexOf(n);
    return i < 0 ? [] : rows.map(r => r[i]).filter(Number.isFinite);
  };

  const out = { file: basename(file), label: tl.label ?? '', gamma: {}, post: {}, pre: {} };
  for (const f of [...BANDS, ...SCALARS]) {
    const v = col(f);
    if (!v.length) continue;
    const post = median(v);
    // Invert the committed curve to recover the normalizer's own output.
    const pre = Math.pow(post, 1 / COMMITTED_GAMMA);
    const target = SYNTHETIC[f];
    const clamped = Math.min(Math.max(pre, 1e-6), 0.999999);
    out.post[f] = post;
    out.pre[f] = pre;
    out.gamma[f] = Math.log(target) / Math.log(clamped);
  }
  return out;
}

const fits = files.map(fit);

/* ---- per capture ------------------------------------------------------- */

for (const f of fits) {
  console.log('');
  console.log(`  ${f.file}`);
  if (f.label) console.log(`  ${f.label}`);
  console.log('  ' + '-'.repeat(58));
  console.log(`  ${'field'.padEnd(12)} ${'p50'.padStart(8)} ${'pre-gamma'.padStart(10)} `
    + `${'target'.padStart(8)} ${'gamma'.padStart(8)}`);
  for (const b of [...BANDS, ...SCALARS]) {
    if (!(b in f.gamma)) continue;
    console.log(`  ${b.padEnd(12)} ${f.post[b].toFixed(3).padStart(8)} `
      + `${f.pre[b].toFixed(3).padStart(10)} ${SYNTHETIC[b].toFixed(3).padStart(8)} `
      + `${f.gamma[b].toFixed(2).padStart(8)}`);
  }
}

/* ---- the part that decides -------------------------------------------- */

if (fits.length < 2) {
  console.log('');
  console.log(`  ONE CAPTURE. A single fit always succeeds and proves nothing — the question`);
  console.log(`  this tool exists to answer is whether independent captures AGREE. Record at`);
  console.log(`  least one more and re-run.`);
  process.exit(0);
}

console.log('');
console.log(`  ACROSS ${fits.length} CAPTURES`);
console.log('  ' + '='.repeat(66));
console.log(`  ${'band'.padEnd(12)} ${'min'.padStart(8)} ${'mean'.padStart(8)} `
  + `${'max'.padStart(8)} ${'spread'.padStart(9)} ${'ratio'.padStart(7)}`);
console.log('  ' + '-'.repeat(66));

let worstSpread = 0, worstBand = '', worstRatio = 1;
const means = {};

for (const b of [...BANDS, ...SCALARS]) {
  const vals = fits.map(f => f.gamma[b]).filter(Number.isFinite);
  if (vals.length < 2) continue;
  const lo = Math.min(...vals), hi = Math.max(...vals);
  const mean = vals.reduce((a, x) => a + x, 0) / vals.length;
  // Spread relative to the mean is the comparable figure; the raw ratio is the headline.
  const spread = mean !== 0 ? (hi - lo) / Math.abs(mean) : 0;
  const ratio = lo > 0 ? hi / lo : Infinity;
  means[b] = mean;
  if (BANDS.includes(b) && spread > worstSpread) { worstSpread = spread; worstBand = b; worstRatio = ratio; }

  const flag = spread <= TOLERANCE ? ' ' : '!';
  console.log(`  ${b.padEnd(12)} ${lo.toFixed(2).padStart(8)} ${mean.toFixed(2).padStart(8)} `
    + `${hi.toFixed(2).padStart(8)} ${(spread * 100).toFixed(0).padStart(8)}% `
    + `${ratio.toFixed(2).padStart(6)}x${flag}`);
}

console.log('');
console.log(`  worst band spread: ${worstBand} at ${(worstSpread * 100).toFixed(0)}% `
  + `(${worstRatio.toFixed(2)}x), tolerance ${(TOLERANCE * 100).toFixed(0)}%`);
console.log('');

if (worstSpread <= TOLERANCE) {
  console.log('  AGREE — the captures are measuring the DSP rather than the material.');
  console.log('  Commit these means to Analyzer.BandGamma, in FF order:');
  console.log('    [' + BANDS.map(b => (means[b] ?? COMMITTED_GAMMA).toFixed(2)).join(', ') + ']');
  console.log(`    rms ${(means.rms ?? COMMITTED_GAMMA).toFixed(2)}  `
    + `peak ${(means.peak ?? COMMITTED_GAMMA).toFixed(2)}`);
  process.exit(0);
}

console.log('  DISAGREE — do not commit the mean. An exponent fitted this loosely bakes one');
console.log('  recording\'s spectral balance into every preset, and the exponent multiplies');
console.log('  the difference rather than averaging it out.');
console.log('');
console.log('  What would resolve it: more captures, and captures of DIFFERENT material of the');
console.log('  same kind — several Dead live recordings rather than one live and one country');
console.log('  record. If Dead captures agree with each other but not with other genres, the');
console.log('  calibration is genre-specific and belongs in the era presets, not the analyzer.');
process.exit(1);
