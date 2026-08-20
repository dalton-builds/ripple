#!/usr/bin/env node
/*
 * tools/make-timeline.mjs
 *
 * CONSTRUCT a Feature Frame timeline from a declarative script, rather than recording
 * one off the synthetic driver and hoping it contains what you need.
 *
 * Recording is fine for general coverage, but it cannot target a specific mapping path:
 * the synthetic driver fires novelty at dt/22 per frame, so 20 s of recording has a
 * coin-flip chance of firing one at all — which is exactly the coverage gap the primary
 * fixture had. A constructed timeline fires the event at frame 120 because the script
 * says frame 120.
 *
 * It is also the natural way to build DSP test material: a script is a readable
 * statement of "what the features look like", checked into the repo next to the fixture
 * it generates.
 *
 * Usage:
 *   node tools/make-timeline.mjs --script tests/fixtures/timeline-novelty.script.json \
 *        --out tests/fixtures/timeline-novelty.json
 *
 *   --script FILE   the script to build from (required)
 *   --out FILE      timeline JSON to write   (default: script path minus .script)
 *   --summary       print per-field min/max/mean of the result
 *
 * SCRIPT FORMAT
 *   {
 *     "kind": "ripple-timeline-script",
 *     "seconds": 20, "rate": 60,
 *     "rhythm": { "bpm": 96, "bpmConf": 0.85, "beatsPerBar": 4,
 *                 "onset": { "tau": 0.15, "peak": 0.9 } },
 *     "baseline": {
 *        "rms": 0.38,                                  // constant
 *        "bass": { "mean": 0.45, "amp": 0.22, "hz": 0.31 },   // sine
 *        "keyHue": { "from": 0.0, "to": 1.0 }           // linear ramp over the run
 *     },
 *     "chroma": { "mode": "triads", "changeEvery": 2.5 },
 *     "events": [
 *        { "field": "novelty", "at": 2.0, "peak": 0.9, "tau": 1.6 },
 *        { "field": "novelty", "at": 7.0, "peak": 1.0, "tau": 1.6, "note": "extreme" }
 *     ]
 *   }
 *
 * Every field name is a Feature Frame field from FIELD_ORDER (see CONTRACTS.md §1).
 * Host-owned indices (time, dt, width, height, aspect, renderScale) are written as zero
 * and overwritten by whichever host replays the file.
 */
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { resolve, dirname } from 'node:path';

/* The 56 fields in declaration order. Must match FF.Names in the native host and
   FIELD_ORDER in the harness — the timeline loader checks it. */
const FIELD_ORDER = [
  'time', 'dt', 'trackPos', 'trackFrac',
  'rms', 'peak', 'loudSlow', 'dynamics',
  'sub', 'bass', 'lowMid', 'mid', 'hiMid', 'presence', 'brilliance', 'air',
  'flux', 'onset', 'beatPhase', 'barPhase',
  'bpm', 'bpmConf', 'beatCount', 'percussivity',
  'ch0', 'ch1', 'ch2', 'ch3', 'ch4', 'ch5', 'ch6', 'ch7', 'ch8', 'ch9', 'ch10', 'ch11',
  'keyHue', 'keyConf', 'chromaFlux', 'centroid',
  'stereoWidth', 'stereoBal', 'novelty', 'sectionPhase',
  'intensity', 'era', 'songMode', 'paletteMix',
  'width', 'height', 'aspect', 'renderScale',
  '_r0', '_r1', '_r2', '_r3',
];
const IDX = Object.fromEntries(FIELD_ORDER.map((n, i) => [n, i]));

/* Indices the host owns on replay; a script must not try to set them. */
const HOST_OWNED = new Set(['time', 'dt', 'width', 'height', 'aspect', 'renderScale']);

/* Major triads on the circle of fifths, as pitch-class triples. Rotating through these
   gives a chord progression that moves chroma and therefore chromaFlux. */
const TRIADS = [
  [0, 4, 7], [7, 11, 2], [2, 6, 9], [9, 1, 4],
  [5, 9, 0], [10, 2, 5], [3, 7, 10], [8, 0, 3],
];

function parseArgs(argv) {
  const o = { script: null, out: null, summary: false, help: false };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => {
      if (i + 1 >= argv.length) throw new Error(`${a} needs a value`);
      return argv[++i];
    };
    switch (a) {
      case '--script': o.script = next(); break;
      case '--out': o.out = next(); break;
      case '--summary': o.summary = true; break;
      case '-h': case '--help': o.help = true; break;
      default: throw new Error(`unknown option: ${a}`);
    }
  }
  if (!o.help && !o.script) throw new Error('--script is required');
  return o;
}

/** A baseline entry: constant, sine, or linear ramp. */
function baselineValue(spec, t, seconds) {
  if (typeof spec === 'number') return spec;
  if (spec.from !== undefined && spec.to !== undefined)
    return spec.from + (spec.to - spec.from) * (seconds > 0 ? t / seconds : 0);
  if (spec.mean !== undefined)
    return spec.mean + (spec.amp || 0) * Math.sin(2 * Math.PI * (spec.hz || 0) * t + (spec.phase || 0));
  throw new Error(`baseline entry must be a number, {mean,amp,hz} or {from,to}: ${JSON.stringify(spec)}`);
}

function build(script, scriptPath) {
  if (script.kind !== 'ripple-timeline-script')
    throw new Error(`${scriptPath}: not a ripple timeline script`);

  const rate = script.rate || 60;
  const seconds = script.seconds || 20;
  const n = Math.round(seconds * rate);
  const dt = 1 / rate;

  const rhythm = script.rhythm || {};
  const bpm = rhythm.bpm || 0;
  const beatsPerBar = rhythm.beatsPerBar || 4;
  const onsetSpec = rhythm.onset || null;

  for (const key of Object.keys(script.baseline || {})) {
    if (!(key in IDX)) throw new Error(`${scriptPath}: unknown baseline field '${key}'`);
    if (HOST_OWNED.has(key))
      throw new Error(`${scriptPath}: '${key}' is host-owned on replay — setting it has no effect`);
  }
  for (const e of script.events || []) {
    if (!(e.field in IDX)) throw new Error(`${scriptPath}: unknown event field '${e.field}'`);
    if (HOST_OWNED.has(e.field))
      throw new Error(`${scriptPath}: '${e.field}' is host-owned on replay`);
  }

  const frames = [];
  const beatsPerSec = bpm / 60;

  for (let i = 0; i < n; i++) {
    const t = i * dt;
    const row = new Array(FIELD_ORDER.length).fill(0);

    // ---- baseline ----
    for (const [key, spec] of Object.entries(script.baseline || {}))
      row[IDX[key]] = baselineValue(spec, t, seconds);

    // ---- rhythm, computed analytically so beat phase is exact ----
    if (bpm > 0) {
      const beats = t * beatsPerSec;
      row[IDX.bpm] = bpm;
      row[IDX.bpmConf] = rhythm.bpmConf ?? 0.85;
      row[IDX.beatCount] = beats;
      const phase = beats - Math.floor(beats);
      row[IDX.beatPhase] = phase;
      row[IDX.barPhase] = (Math.floor(beats) % beatsPerBar + phase) / beatsPerBar;

      // Onset impulse decaying from each beat, matching the DSP's ~150 ms envelope.
      if (onsetSpec) {
        const sinceBeat = phase / Math.max(beatsPerSec, 1e-6);
        row[IDX.onset] = Math.max(row[IDX.onset],
          (onsetSpec.peak ?? 0.9) * Math.exp(-sinceBeat / (onsetSpec.tau ?? 0.15)));
      }
    }

    // ---- chroma: rotate through triads, and pulse chromaFlux on each change ----
    if (script.chroma?.mode === 'triads') {
      const every = script.chroma.changeEvery || 2.5;
      const step = Math.floor(t / every);
      const triad = TRIADS[step % TRIADS.length];
      for (const pc of triad) row[IDX['ch' + pc]] = 1;
      // Fifths and thirds get some energy too, so the chroma vector is not a bare
      // three-hot — real chroma never is, and key detection keys off the shape.
      for (const pc of triad) row[IDX['ch' + ((pc + 7) % 12)]] = Math.max(row[IDX['ch' + ((pc + 7) % 12)]], 0.35);

      const sinceChange = t - step * every;
      row[IDX.chromaFlux] = Math.max(row[IDX.chromaFlux],
        (script.chroma.fluxPeak ?? 0.85) * Math.exp(-sinceChange / (script.chroma.fluxTau ?? 0.45)));
      row[IDX.keyConf] = script.chroma.keyConf ?? 0.7;
      if (script.chroma.keyFromTriad !== false) row[IDX.keyHue] = (step % TRIADS.length) / TRIADS.length;
    }

    // ---- scripted events: exponential decay pulses, max-combined ----
    for (const e of script.events || []) {
      if (t < e.at) continue;
      const v = (e.peak ?? 1) * Math.exp(-(t - e.at) / (e.tau ?? 1));
      if (v < 1e-4) continue;
      row[IDX[e.field]] = Math.max(row[IDX[e.field]], v);
    }

    // ---- section phase runs over the whole timeline unless scripted ----
    if (script.baseline?.sectionPhase === undefined) row[IDX.sectionPhase] = t / seconds;
    if (script.baseline?.trackPos === undefined) row[IDX.trackPos] = t;
    if (script.baseline?.trackFrac === undefined) row[IDX.trackFrac] = t / seconds;
    if (script.baseline?.intensity === undefined) row[IDX.intensity] = 1;

    // Clamp to the contract: every field is 0..1 except the ones marked RAW.
    const RAW = new Set([IDX.bpm, IDX.beatCount, IDX.stereoBal, IDX.era, IDX.songMode,
                         IDX.trackPos, IDX.time, IDX.dt,
                         IDX.width, IDX.height, IDX.aspect, IDX.renderScale]);
    for (let k = 0; k < row.length; k++) {
      if (RAW.has(k)) continue;
      row[k] = Math.min(1, Math.max(0, row[k]));
    }
    // Reserved must stay zero-filled — schemaVersion 2+ growth space.
    row[52] = row[53] = row[54] = row[55] = 0;

    frames.push(row.map(v => Math.round(v * 1e5) / 1e5));
  }

  return {
    schemaVersion: 1,
    kind: 'ripple-feature-timeline',
    generator: 'ripple-make-timeline/1',
    constructed: true,
    script: scriptPath.replace(/\\/g, '/').split('/').slice(-1)[0],
    note: script.note || 'Constructed by tools/make-timeline.mjs from a committed script. '
        + 'Regenerate rather than editing by hand.',
    rate,
    seconds: +(frames.length / rate).toFixed(3),
    frameBytes: FIELD_ORDER.length * 4,
    fieldOrder: FIELD_ORDER,
    frames,
  };
}

function summarize(tl, fields) {
  console.log('');
  console.log(`  ${'field'.padEnd(14)} ${'min'.padStart(8)} ${'max'.padStart(8)} ${'mean'.padStart(8)}`);
  console.log('  ' + '-'.repeat(42));
  for (const f of fields) {
    const k = IDX[f];
    let mn = Infinity, mx = -Infinity, sum = 0;
    for (const r of tl.frames) { mn = Math.min(mn, r[k]); mx = Math.max(mx, r[k]); sum += r[k]; }
    console.log(`  ${f.padEnd(14)} ${mn.toFixed(3).padStart(8)} ${mx.toFixed(3).padStart(8)} `
      + `${(sum / tl.frames.length).toFixed(3).padStart(8)}`);
  }
  console.log('');
}

function main() {
  let opt;
  try { opt = parseArgs(process.argv.slice(2)); }
  catch (e) { console.error(`make-timeline: ${e.message}`); return 2; }
  if (opt.help) {
    console.log('make-timeline.mjs --script FILE [--out FILE] [--summary]');
    return 0;
  }

  const scriptPath = resolve(opt.script);
  const script = JSON.parse(readFileSync(scriptPath, 'utf8'));
  const tl = build(script, scriptPath);

  const out = resolve(opt.out || scriptPath.replace(/\.script\.json$/, '.json'));
  mkdirSync(dirname(out), { recursive: true });
  writeFileSync(out, JSON.stringify(tl));

  console.log(`make-timeline: ${tl.frames.length} frames @ ${tl.rate} fps `
    + `(${tl.seconds}s) -> ${out}`);

  if (opt.summary) {
    const touched = new Set([
      ...Object.keys(script.baseline || {}),
      ...(script.events || []).map(e => e.field),
      'onset', 'beatPhase', 'bpm', 'chromaFlux', 'keyHue', 'sectionPhase',
    ]);
    summarize(tl, [...touched].filter(f => f in IDX));
  }
  return 0;
}

try { process.exit(main()); }
catch (e) { console.error(`make-timeline: ${e.stack || e.message}`); process.exit(2); }
