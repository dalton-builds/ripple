#!/usr/bin/env node
/*
 * tools/timeline-stats.mjs
 *
 * Summarize one or more Feature Frame timelines: per-field distributions, tempo and
 * confidence behaviour, and how much of the run fell through to §7.3's free-time fallback.
 *
 * Two jobs. Immediately after a capture it answers "is this take usable" — level sane, all
 * eight bands carrying content, tempo doing something plausible — while there is still time
 * to redo it. Across several captures it is the measurement instrument: pass more than one
 * file and it prints a comparison table, which is what a normalization change or an octave
 * disambiguation change gets judged against. For the specific question of whether two
 * RECORDING ERAS agree, use tools/cross-era.mjs — it grades the between-era gap against the
 * within-era spread, which is the comparison that has an answer.
 *
 * Dependency-free, like the rest of tools/.
 *
 * Usage:
 *   node tools/timeline-stats.mjs <timeline.json> [more.json ...] [options]
 *
 *   --fields a,b,c   only these fields
 *   --csv            machine-readable, one row per file per field
 *   --quiet          skip the per-file detail, print only the comparison
 */
import { readFileSync } from 'node:fs';
import { basename } from 'node:path';

/* There used to be a `--gamma N` what-if here, which recovered the raw normalizer output as
   v^(1/4.66) and re-exponentiated it to preview a different LevelGamma. It is gone because the
   exponent is gone: §7.1 now normalizes by percentile within a rolling history, so there is no
   committed curve to invert and the option would have quietly reported fiction about every
   capture recorded after that change. The question it was built to answer — does this
   calibration hold across recordings — is now asked properly by tools/cross-era.mjs. */

/* Fields the calibration applies to — level-type only. Must match Analyzer: the eight
   bands plus rms, peak and loudSlow. */
const LEVEL_FIELDS = new Set([
  'sub', 'bass', 'lowMid', 'mid', 'hiMid', 'presence', 'brilliance', 'air',
  'rms', 'peak', 'loudSlow',
]);

const DEFAULT_FIELDS = [
  'sub', 'bass', 'lowMid', 'mid', 'hiMid', 'presence', 'brilliance', 'air',
  'rms', 'peak', 'loudSlow', 'dynamics', 'flux', 'onset', 'percussivity',
  'centroid', 'stereoWidth', 'chromaFlux', 'keyConf', 'novelty',
];

function parseArgs(argv) {
  const o = { files: [], fields: DEFAULT_FIELDS, csv: false, quiet: false };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => {
      if (i + 1 >= argv.length) throw new Error(`${a} needs a value`);
      return argv[++i];
    };
    switch (a) {
      case '--fields': o.fields = next().split(',').map(s => s.trim()); break;
      case '--csv': o.csv = true; break;
      case '--quiet': o.quiet = true; break;
      case '-h': case '--help': o.help = true; break;
      default:
        if (a.startsWith('--')) throw new Error(`unknown option: ${a}`);
        o.files.push(a);
    }
  }
  if (!o.help && !o.files.length) throw new Error('need at least one timeline');
  return o;
}

function load(path) {
  const t = JSON.parse(readFileSync(path, 'utf8'));
  if (t.kind !== 'ripple-feature-timeline') throw new Error(`${path}: not a feature timeline`);
  const idx = {};
  (t.fieldOrder || []).forEach((n, i) => { idx[n] = i; });
  return { path, t, idx };
}

const pct = (sorted, p) => {
  if (!sorted.length) return NaN;
  const x = p * (sorted.length - 1);
  const i = Math.floor(x);
  return i + 1 < sorted.length ? sorted[i] + (sorted[i + 1] - sorted[i]) * (x - i) : sorted[i];
};

function column(tl, field) {
  const c = tl.idx[field];
  if (c === undefined) return null;
  return tl.t.frames.map(r => r[c]);
}

function stats(values) {
  const v = values.slice().sort((a, b) => a - b);
  const mean = v.reduce((a, b) => a + b, 0) / (v.length || 1);
  return {
    n: v.length, min: v[0], max: v[v.length - 1], mean,
    p10: pct(v, 0.10), p50: pct(v, 0.50), p90: pct(v, 0.90),
  };
}

/**
 * Tempo behaviour, which is the thing a capture is most likely to reveal a problem in.
 *
 * bpmConfidence < 0.35 is §7.3's fallback threshold: below it the phase accumulator freezes
 * and beat phase comes from the decaying onset impulse. So "fraction of frames below 0.35"
 * is not a quality score — it is the answer to "did this passage read as pulsed or as free
 * time", and the right answer differs per capture. Drums>Space SHOULD be ~100%.
 */
function tempoReport(tl) {
  const conf = column(tl, 'bpmConf');
  const bpm = column(tl, 'bpm');
  if (!conf || !bpm) return null;

  const rate = tl.t.rate || 60;
  const below = conf.filter(c => c < 0.35).length;

  // BPM is only meaningful where the tracker was confident; averaging it across free-time
  // frames mixes in numbers the DSP itself does not believe.
  const locked = [];
  for (let i = 0; i < bpm.length; i++) if (conf[i] >= 0.35 && bpm[i] > 0) locked.push(bpm[i]);
  const l = locked.slice().sort((a, b) => a - b);

  // Drift: compare the median of the first and last thirds of the LOCKED frames, which is
  // what "tracks with drift" has to mean for a tracker that reports nothing when unsure.
  let drift = null;
  if (locked.length > 60) {
    const third = Math.floor(locked.length / 3);
    const first = locked.slice(0, third).sort((a, b) => a - b);
    const last = locked.slice(-third).sort((a, b) => a - b);
    drift = pct(last, 0.5) - pct(first, 0.5);
  }

  return {
    seconds: tl.t.frames.length / rate,
    belowFraction: below / conf.length,
    confMean: conf.reduce((a, b) => a + b, 0) / conf.length,
    confP50: pct(conf.slice().sort((a, b) => a - b), 0.5),
    confP90: pct(conf.slice().sort((a, b) => a - b), 0.9),
    lockedFraction: locked.length / conf.length,
    bpmP50: l.length ? pct(l, 0.5) : NaN,
    bpmP10: l.length ? pct(l, 0.1) : NaN,
    bpmP90: l.length ? pct(l, 0.9) : NaN,
    drift,
  };
}

const f3 = v => (Number.isFinite(v) ? v.toFixed(3) : '  --  ');
const f1 = v => (Number.isFinite(v) ? v.toFixed(1) : ' -- ');
const pad = (s, n, right = false) => right ? String(s).padEnd(n) : String(s).padStart(n);

function main() {
  let opt;
  try { opt = parseArgs(process.argv.slice(2)); }
  catch (e) { console.error(`timeline-stats: ${e.message}`); return 2; }
  if (opt.help) {
    console.log('timeline-stats.mjs <timeline.json> [...] [--fields a,b] [--csv] '
      + '[--quiet]');
    return 0;
  }

  const tls = opt.files.map(load);

  /* ---------------- per file ---------------- */
  if (!opt.quiet && !opt.csv) {
    for (const tl of tls) {
      const label = tl.t.label ? ` — ${tl.t.label}` : '';
      console.log('');
      console.log(`  ${basename(tl.path)}${label}`);
      console.log(`  ${'-'.repeat(Math.min(78, basename(tl.path).length + label.length + 2))}`);
      console.log(`  ${tl.t.frames.length} frames @ ${tl.t.rate} fps `
        + `(${(tl.t.frames.length / (tl.t.rate || 60)).toFixed(1)}s)`
        + (tl.t.sampleRate ? `, captured at ${tl.t.sampleRate} Hz` : '')
        + (tl.t.source ? `, ${tl.t.source}` : ''));

      const tr = tempoReport(tl);
      if (tr) {
        console.log('');
        console.log(`  tempo:  locked on ${(tr.lockedFraction * 100).toFixed(1)}% of frames, `
          + `below the 0.35 fallback on ${(tr.belowFraction * 100).toFixed(1)}%`);
        console.log(`          confidence  mean ${f3(tr.confMean)}  p50 ${f3(tr.confP50)}  `
          + `p90 ${f3(tr.confP90)}`);
        if (Number.isFinite(tr.bpmP50))
          console.log(`          BPM where locked  p10 ${f1(tr.bpmP10)}  p50 ${f1(tr.bpmP50)}  `
            + `p90 ${f1(tr.bpmP90)}`
            + (tr.drift !== null ? `   drift first->last third: ${tr.drift >= 0 ? '+' : ''}${f1(tr.drift)} BPM` : ''));
        else
          console.log('          never locked — no BPM estimate the tracker believed');
      }

      console.log('');
      console.log(`  ${pad('field', 13, true)} ${pad('min', 7)} ${pad('p10', 7)} `
        + `${pad('p50', 7)} ${pad('p90', 7)} ${pad('max', 7)} ${pad('mean', 7)}`);
      console.log('  ' + '-'.repeat(62));
      for (const name of opt.fields) {
        const col = column(tl, name);
        if (!col) continue;
        const s = stats(col);
        console.log(`  ${pad(name, 13, true)} ${pad(f3(s.min), 7)} ${pad(f3(s.p10), 7)} `
          + `${pad(f3(s.p50), 7)} ${pad(f3(s.p90), 7)} ${pad(f3(s.max), 7)} ${pad(f3(s.mean), 7)}`);
      }
    }
  }

  /* ---------------- comparison ---------------- */
  if (tls.length > 1 && !opt.csv) {
    console.log('');
    console.log('  MEDIANS ACROSS CAPTURES');
    const w = Math.max(...tls.map(t => basename(t.path).replace(/\.json$/, '').length), 8);
    console.log(`  ${pad('field', 13, true)} `
      + tls.map(t => pad(basename(t.path).replace(/\.json$/, ''), w)).join(' '));
    console.log('  ' + '-'.repeat(14 + tls.length * (w + 1)));
    for (const name of opt.fields) {
      const cells = tls.map(tl => {
        const col = column(tl, name);
        return col ? pad(f3(pct(col.slice().sort((a, b) => a - b), 0.5)), w) : pad('--', w);
      });
      console.log(`  ${pad(name, 13, true)} ` + cells.join(' '));
    }
  }

  /* ---------------- csv ---------------- */
  if (opt.csv) {
    console.log('file,field,n,min,p10,p50,p90,max,mean');
    for (const tl of tls) {
      for (const name of opt.fields) {
        const col = column(tl, name);
        if (!col) continue;
        const s = stats(col);
        console.log([basename(tl.path), name, s.n, s.min, s.p10, s.p50, s.p90, s.max, s.mean]
          .map(v => (typeof v === 'number' ? v.toFixed(6) : v)).join(','));
      }
    }
  }

  console.log('');
  return 0;
}

try { process.exit(main()); }
catch (e) { console.error(`timeline-stats: ${e.stack || e.message}`); process.exit(2); }
