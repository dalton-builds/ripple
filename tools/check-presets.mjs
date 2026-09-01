#!/usr/bin/env node
/*
 * tools/check-presets.mjs
 *
 * The era presets exist TWICE: embedded as `ERA_PRESETS` in harness/index.html, and on disk
 * as presets/eras/*.json. Both hosts render from their own copy, so a field edited in one and
 * not the other makes the two hosts render different images from nominally the same preset.
 *
 * WHY THIS TOOL EXISTS. `extract-shaders.mjs --check` guards the shaders and
 * `extract-defaults.mjs --check` guards PRESET_DEFAULTS, but nothing guarded the era presets
 * themselves. Adding four rhythmic mapping routes to the JSON files and not to the harness
 * took the parity gate from mean SSIM 1.0000 to 0.9493 with a min of 0.4674 — and the failure
 * gave no hint of the cause, because SSIM cannot say "your preset tables disagree". The trace
 * diff named it in one run (warpAmount 0.4016 vs 0.1807), which is the documented first move
 * on a parity failure and the reason it is documented.
 *
 * Compares the fields that AFFECT RENDERING. Deliberately not a byte diff: the disk files are
 * pretty-printed and carry `_`-prefixed comment keys that PresetStore.Strip drops, and a tool
 * that fails on formatting is a tool people stop running.
 *
 * Usage:
 *   node tools/check-presets.mjs [--in harness/index.html] [--presets presets/eras]
 *
 * Exits nonzero on any disagreement.
 */
import { readFileSync, readdirSync } from 'node:fs';
import { join, resolve, dirname, basename } from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const argv = process.argv.slice(2);
const arg = (n, d) => { const i = argv.indexOf('--' + n); return i === -1 ? d : argv[i + 1]; };

const htmlPath = resolve(REPO, arg('in', 'harness/index.html'));
const erasDir = resolve(REPO, arg('presets', 'presets/eras'));

/* Pull the ERA_PRESETS array literal out of the harness. It is plain JSON per element, so
   slicing between the brackets and parsing is enough — no JS evaluation needed. */
function harnessEras(html) {
  const marker = 'const ERA_PRESETS = [';
  const start = html.indexOf(marker);
  if (start === -1) throw new Error('ERA_PRESETS not found in the harness');
  const open = start + marker.length - 1;
  let depth = 0, end = -1;
  for (let i = open; i < html.length; i++) {
    if (html[i] === '[') depth++;
    else if (html[i] === ']') { depth--; if (depth === 0) { end = i + 1; break; } }
  }
  if (end === -1) throw new Error('ERA_PRESETS array is not closed');
  return JSON.parse(html.slice(open, end));
}

/* Strip the keys that cannot affect rendering, so formatting and comments do not fail the
   check. Mirrors PresetStore.Strip: anything starting with `_`. */
function renderable(o) {
  if (Array.isArray(o)) return o.map(renderable);
  if (o === null || typeof o !== 'object') return o;
  const out = {};
  for (const k of Object.keys(o).sort()) {
    if (k.startsWith('_')) continue;
    out[k] = renderable(o[k]);
  }
  return out;
}

const html = readFileSync(htmlPath, 'utf8');
const fromHarness = new Map(harnessEras(html).map(p => [p.id, p]));

const fromDisk = new Map();
for (const f of readdirSync(erasDir).filter(f => f.endsWith('.json'))) {
  const p = JSON.parse(readFileSync(join(erasDir, f), 'utf8'));
  fromDisk.set(p.id, { preset: p, file: basename(f) });
}

const problems = [];

for (const [id, h] of fromHarness) {
  const d = fromDisk.get(id);
  if (!d) { problems.push(`${id}: in the harness, missing from ${erasDir}`); continue; }

  /* Mapping first and separately: it is the field most likely to drift, because it is the one
     people edit to change behaviour, and order matters — several targets accumulate. */
  const hm = JSON.stringify(Object.entries(h.mapping ?? {}));
  const dm = JSON.stringify(Object.entries(d.preset.mapping ?? {}));
  if (hm !== dm) {
    problems.push(`${id}: mapping differs\n      harness ${hm}\n      disk    ${dm}`);
  }

  const hr = JSON.stringify(renderable({ ...h, mapping: undefined }), null, 1);
  const dr = JSON.stringify(renderable({ ...d.preset, mapping: undefined }), null, 1);
  if (hr !== dr) {
    const hl = hr.split('\n'), dl = dr.split('\n');
    const first = hl.findIndex((l, i) => l !== dl[i]);
    problems.push(`${id}: fields differ (${d.file}), first at line ${first + 1}\n`
      + `      harness ${(hl[first] ?? '(end)').trim()}\n`
      + `      disk    ${(dl[first] ?? '(end)').trim()}`);
  }
}

for (const [id] of fromDisk) {
  if (!fromHarness.has(id)) problems.push(`${id}: on disk, missing from the harness`);
}

console.log(`  ${fromHarness.size} era preset(s) in the harness, ${fromDisk.size} on disk`);
if (problems.length === 0) {
  console.log('\n  presets/eras matches the harness');
  process.exit(0);
}
for (const p of problems) console.log(`  DIFF  ${p}`);
console.log(`\n  ${problems.length} disagreement(s) — the two hosts would render differently`);
process.exit(1);
