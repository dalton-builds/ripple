#!/usr/bin/env node
/*
 * tools/compare-trace.mjs
 *
 * Diff the per-frame draw-state traces the two hosts write with --trace.
 *
 * SSIM tells you two renders disagree. This tells you WHICH parameter did, and
 * on which frame it first drifted — the difference between "0.86 mean SSIM" and
 * "safeScale diverges from frame 43 because the limiter engaged on one host and
 * not the other". Run it before reaching for the diff images: a host-side
 * parameter divergence is far more likely than a shader one, and it is the only
 * kind that can be fixed without touching the shared shaders.
 *
 * Usage:
 *   node tools/compare-trace.mjs <dirA> <dirB> [--tol N] [--top N]
 *
 *   --tol N   relative tolerance before a difference counts   (default 1e-4)
 *   --top N   parameters to list per preset                   (default 12)
 *
 * Each directory is either a preset directory holding trace.json, or a dump
 * root holding one preset subdirectory per preset.
 */
import { readdirSync, readFileSync, existsSync, statSync } from 'node:fs';
import { join, resolve, basename } from 'node:path';

function parseArgs(argv) {
  const o = { dirs: [], tol: 1e-4, top: 12 };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--tol') o.tol = Number(argv[++i]);
    else if (a === '--top') o.top = Number(argv[++i]);
    else if (a === '-h' || a === '--help') o.help = true;
    else if (a.startsWith('--')) throw new Error(`unknown option: ${a}`);
    else o.dirs.push(a);
  }
  if (!o.help && o.dirs.length !== 2) throw new Error('need two directories');
  return o;
}

function groupsIn(root) {
  if (existsSync(join(root, 'trace.json'))) return [{ id: basename(root), dir: root }];
  return readdirSync(root)
    .filter(n => statSync(join(root, n)).isDirectory())
    .filter(n => existsSync(join(root, n, 'trace.json')))
    .sort()
    .map(id => ({ id, dir: join(root, id) }));
}

const pad = (s, n, right = false) => right ? String(s).padEnd(n) : String(s).padStart(n);

function main() {
  const opt = parseArgs(process.argv.slice(2));
  if (opt.help) {
    console.log('compare-trace.mjs <dirA> <dirB> [--tol N] [--top N]');
    return 0;
  }

  const [rootA, rootB] = opt.dirs.map(d => resolve(d));
  const groups = groupsIn(rootA);
  if (!groups.length) { console.error(`no trace.json under ${rootA}`); return 2; }

  let anyDiff = false;

  for (const g of groups) {
    const pathB = groups.length === 1 && existsSync(join(rootB, 'trace.json'))
      ? join(rootB, 'trace.json') : join(rootB, g.id, 'trace.json');
    if (!existsSync(pathB)) { console.log(`${g.id}: no trace in B`); continue; }

    const A = JSON.parse(readFileSync(join(g.dir, 'trace.json'), 'utf8'));
    const B = JSON.parse(readFileSync(pathB, 'utf8'));

    if (A.keys.join(',') !== B.keys.join(',')) {
      console.log(`${g.id}: KEY MISMATCH — the two hosts' trace orders differ`);
      anyDiff = true;
      continue;
    }

    const n = Math.min(A.rows.length, B.rows.length);
    const stats = A.keys.map(k => ({ key: k, maxRel: 0, maxAbs: 0, firstFrame: -1, a: 0, b: 0 }));

    for (let i = 0; i < n; i++) {
      for (let k = 0; k < A.keys.length; k++) {
        const a = A.rows[i][k], b = B.rows[i][k];
        const abs = Math.abs(a - b);
        // Relative to the larger magnitude, with a floor so a parameter that
        // is zero on one side still registers.
        const rel = abs / Math.max(1e-6, Math.abs(a), Math.abs(b));
        const s = stats[k];
        if (rel > opt.tol && s.firstFrame < 0) { s.firstFrame = i; s.a = a; s.b = b; }
        if (rel > s.maxRel) { s.maxRel = rel; s.maxAbs = abs; }
      }
    }

    const bad = stats.filter(s => s.firstFrame >= 0)
      .sort((p, q) => (p.firstFrame - q.firstFrame) || (q.maxRel - p.maxRel));

    console.log('');
    console.log(`  ${g.id}  (${n} frames, tol ${opt.tol})`);
    if (!bad.length) {
      console.log('    every traced parameter matches on every frame');
      continue;
    }
    anyDiff = true;
    console.log(`    ${pad('parameter', 16, true)}  first@  ${pad('A', 12)}  ${pad('B', 12)}  ${pad('max rel', 10)}`);
    for (const s of bad.slice(0, opt.top)) {
      console.log(`    ${pad(s.key, 16, true)}  ${pad(s.firstFrame, 6)}  `
        + `${pad(s.a.toPrecision(6), 12)}  ${pad(s.b.toPrecision(6), 12)}  ${pad(s.maxRel.toExponential(2), 10)}`);
    }
    if (bad.length > opt.top) console.log(`    ... and ${bad.length - opt.top} more`);
  }

  console.log('');
  console.log(anyDiff ? '  TRACES DIFFER' : '  TRACES MATCH');
  console.log('');
  return anyDiff ? 1 : 0;
}

try { process.exit(main()); }
catch (e) { console.error(`compare-trace: ${e.message}`); process.exit(2); }
