#!/usr/bin/env node
/*
 * tools/cross-era.mjs
 *
 * The measurement §7.1's normalization has to survive: do two DIFFERENT RECORDING ERAS of the
 * same band read the same?
 *
 * This is the instrument that retired the fitted exponent. `fit-band-gamma.mjs` asked "what
 * exponent makes this capture match the synthetic driver", per capture, and the answers
 * disagreed by 30x on the air band because a 1966 desk feed has no content up there to fit.
 * That is not a question with an answer. The question with an answer is the one here.
 *
 * THE TEST IS NOT "do the eras agree exactly". Two different songs have genuinely different
 * spectral balance, and a normalizer that flattened that out would be destroying the signal it
 * exists to deliver. The test is whether the disagreement BETWEEN eras is smaller than the
 * disagreement WITHIN one era. If it is, the residual variation is musical rather than
 * technological, and the recording medium has stopped leaking into the visuals — which is the
 * whole property. If between-era exceeds within-era, the tape is still showing through.
 *
 * Usage:
 *   node tools/cross-era.mjs --era "1966=a.json,b.json" --era "2013=c.json,d.json"
 *
 *   --fields a,b,c   only these fields
 *   --tol N          flag a field when between-era exceeds within-era by more than N
 *                    (default 0.05, about the width of a preset slider's useful resolution)
 */
import { readFileSync } from 'node:fs';
import { basename } from 'node:path';

/* Field order is the Feature Frame's, and it is CONTRACTS §1 — not restated here. Read it
   from the capture so this tool cannot drift out of sync with the recorder. */
function load(path) {
  const doc = JSON.parse(readFileSync(path, 'utf8'));
  const meta = doc.meta ?? doc;
  const names = meta.fieldOrder ?? doc.fieldOrder;
  if (!names) throw new Error(`${basename(path)}: no fieldOrder — not a Feature Frame capture`);
  const frames = meta.frames ?? doc.frames;
  if (!frames?.length) throw new Error(`${basename(path)}: no frames`);
  return { path, names, frames, label: meta.label ?? basename(path) };
}

function median(xs) {
  const s = [...xs].sort((a, b) => a - b);
  const m = s.length >> 1;
  return s.length % 2 ? s[m] : (s[m - 1] + s[m]) / 2;
}

const args = process.argv.slice(2);
const eras = [];
let only = null, tol = 0.05;
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--era') {
    const [name, list] = args[++i].split('=');
    eras.push({ name, files: list.split(',').map(s => s.trim()).filter(Boolean) });
  } else if (args[i] === '--fields') only = new Set(args[++i].split(','));
  else if (args[i] === '--tol') tol = Number(args[++i]);
  else { console.error(`cross-era: unknown argument ${args[i]}`); process.exit(2); }
}
if (eras.length < 2) {
  console.error('cross-era: need at least two --era groups to compare');
  process.exit(2);
}

for (const era of eras) {
  era.caps = era.files.map(load);
  if (era.caps.length < 2) {
    console.error(`cross-era: era "${era.name}" has ${era.caps.length} capture — within-era `
      + 'spread needs at least two, or there is nothing to compare the between-era gap against');
    process.exit(2);
  }
}

/* One field list, and every capture must share it. A capture recorded before a field was added
   would silently shift every column right. */
const names = eras[0].caps[0].names;
for (const era of eras)
  for (const c of era.caps)
    if (c.names.join() !== names.join())
      throw new Error(`${basename(c.path)}: field list differs — captures are not comparable`);

for (const era of eras)
  for (const c of era.caps) {
    c.medians = names.map((_, i) => median(c.frames.map(f => f[i])));
  }

/*
 * Not every Feature Frame slot is a measurement of the audio, and the ones that are not would
 * dominate this table with numbers that mean nothing here:
 *
 *   CLOCKS AND COUNTERS — trackPos, trackFrac, beatCount, and the phase ramps. These grow with
 *   elapsed time, so their "between-era gap" is really the difference in how far into each
 *   track the capture started. trackPos flagged at 975 against a within spread of 317 on the
 *   first run, which says nothing about normalization and everything about when I hit record.
 *
 *   CONTROL INPUTS — era, songMode, paletteMix, intensity, sectionPhase. These are set by the
 *   host from metadata and config, not derived from the signal. They are identical across
 *   captures by construction and pad the pass count with free wins.
 */
const NOT_MEASURED = /^(trackPos|trackFrac|beatCount|era|songMode|paletteMix|intensity|sectionPhase)$/;

const fields = names
  .map((n, i) => ({ n, i }))
  .filter(({ n }) => (!only || only.has(n))
    && !/^_|^time$|^dt$|^beatPhase|^barPhase/.test(n)
    && !NOT_MEASURED.test(n));

console.log();
console.log(`  CROSS-ERA AGREEMENT — ${eras.map(e => `${e.name} (${e.caps.length})`).join(' vs ')}`);
console.log(`  between-era gap of medians against within-era spread; flagged when between `
  + `exceeds within by > ${tol.toFixed(2)}`);
console.log();
const w = Math.max(12, ...fields.map(f => f.n.length));
console.log('  ' + 'field'.padEnd(w)
  + eras.map(e => e.name.padStart(10)).join('')
  + 'between'.padStart(10) + 'within'.padStart(9) + '  verdict');
console.log('  ' + '-'.repeat(w + eras.length * 10 + 19 + 12));

let flagged = 0, agree = 0;
const rows = [];
for (const f of fields) {
  const eraMeans = eras.map(e => e.caps.reduce((a, c) => a + c.medians[f.i], 0) / e.caps.length);

  // Between: the widest gap between any two era means. Within: the widest spread inside any
  // single era. Taking the max on both sides rather than an average keeps one badly behaved
  // era from being averaged into looking fine.
  let between = 0;
  for (let a = 0; a < eraMeans.length; a++)
    for (let b = a + 1; b < eraMeans.length; b++)
      between = Math.max(between, Math.abs(eraMeans[a] - eraMeans[b]));

  let within = 0;
  for (const e of eras) {
    const ms = e.caps.map(c => c.medians[f.i]);
    within = Math.max(within, Math.max(...ms) - Math.min(...ms));
  }

  const bad = between - within > tol;
  if (bad) flagged++; else agree++;
  rows.push({ f, eraMeans, between, within, bad });

  console.log('  ' + f.n.padEnd(w)
    + eraMeans.map(v => v.toFixed(3).padStart(10)).join('')
    + between.toFixed(3).padStart(10) + within.toFixed(3).padStart(9)
    + '  ' + (bad ? 'MEDIUM SHOWS THROUGH' : 'musical'));
}

console.log();
console.log(`  ${agree}/${fields.length} fields: between-era gap within the within-era spread`);
if (flagged) {
  console.log();
  console.log('  Fields where the recording medium still shows through:');
  for (const r of rows.filter(r => r.bad))
    console.log(`    ${r.f.n.padEnd(w)} between ${r.between.toFixed(3)} `
      + `vs within ${r.within.toFixed(3)}`);
}
console.log();
process.exit(flagged ? 1 : 0);
