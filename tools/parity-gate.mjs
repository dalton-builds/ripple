#!/usr/bin/env node
/*
 * tools/parity-gate.mjs
 *
 * Run the whole cross-host parity gate: dump the timeline from the harness, dump it
 * from the native host, compare, exit nonzero if the gate fails.
 *
 * This exists so the gate is one command rather than three remembered ones. The
 * exclusions, dump geometry and thresholds all come from tests/parity-config.json, and
 * both hosts are handed the same mode — so a re-run after Phase 4 touches the composite
 * pass measures the same experiment that set the baseline, without anyone having to
 * recall which --no-* flags were in play.
 *
 * Usage:
 *   node tools/parity-gate.mjs --mode a
 *   node tools/parity-gate.mjs --mode a --timeline tests/fixtures/timeline-novelty.json
 *   node tools/parity-gate.mjs --mode b --preset era-brent
 *   node tools/parity-gate.mjs --all-timelines --mode a
 *
 * Options:
 *   --mode a|b           gate mode from the config              (default a)
 *   --timeline FILE      timeline to replay      (default: config's "primary" entry)
 *   --all-timelines      run every timeline the config lists
 *   --preset ID          one preset instead of the whole library
 *   --out DIR            working directory for dumps            (default captures/gate)
 *   --parity-config F    config path
 *   --skip-harness       reuse an existing harness dump
 *   --skip-native        reuse an existing native dump
 *   --keep               do not delete previous dumps first
 */
import { spawnSync } from 'node:child_process';
import { existsSync, rmSync, mkdirSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { loadParityConfig, parityMode } from './parity-config.mjs';

const REPO = resolve(dirname(fileURLToPath(import.meta.url)), '..');

function parseArgs(argv) {
  const o = {
    mode: 'a', timeline: null, allTimelines: false, preset: null,
    out: 'captures/gate', parityConfig: 'tests/parity-config.json',
    skipHarness: false, skipNative: false, keep: false, help: false,
  };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => {
      if (i + 1 >= argv.length) throw new Error(`${a} needs a value`);
      return argv[++i];
    };
    switch (a) {
      case '--mode': o.mode = next().toLowerCase(); break;
      case '--timeline': o.timeline = next(); break;
      case '--all-timelines': o.allTimelines = true; break;
      case '--preset': o.preset = next(); break;
      case '--out': o.out = next(); break;
      case '--parity-config': o.parityConfig = next(); break;
      case '--skip-harness': o.skipHarness = true; break;
      case '--skip-native': o.skipNative = true; break;
      case '--keep': o.keep = true; break;
      case '-h': case '--help': o.help = true; break;
      default: throw new Error(`unknown option: ${a}`);
    }
  }
  return o;
}

/** Inherit stdio: the sub-tools' own progress output is the progress output. */
function run(cmd, args, label) {
  console.log(`\n── ${label}\n   ${cmd} ${args.join(' ')}\n`);
  const r = spawnSync(cmd, args, { cwd: REPO, stdio: 'inherit', shell: false });
  if (r.error) throw new Error(`${label}: ${r.error.message}`);
  return r.status ?? 1;
}

/**
 * The native binary, preferring Release. Built by build.ps1; this tool does not build,
 * so a stale binary is the caller's problem and saying which one ran matters.
 */
function nativeExe() {
  const tfm = 'net10.0-windows10.0.19041.0';
  for (const cfg of ['Release', 'Debug']) {
    const p = join(REPO, 'src', 'Ripple', 'bin', cfg, tfm, 'win-x64', 'Ripple.exe');
    if (existsSync(p)) return p;
  }
  throw new Error('no Ripple.exe found — run .\\build.ps1 first');
}

function main() {
  let opt;
  try { opt = parseArgs(process.argv.slice(2)); }
  catch (e) { console.error(`parity-gate: ${e.message}`); return 2; }
  if (opt.help) {
    console.log('parity-gate.mjs --mode a|b [--timeline FILE] [--all-timelines] '
      + '[--preset ID] [--out DIR] [--skip-harness] [--skip-native] [--keep]');
    return 0;
  }

  const cfgPath = resolve(REPO, opt.parityConfig);
  const cfg = loadParityConfig(cfgPath);
  const m = parityMode(cfg, opt.mode, cfgPath);

  let timelines;
  if (opt.timeline) timelines = [{ path: opt.timeline, role: 'explicit' }];
  else if (opt.allTimelines) timelines = cfg.timelines || [];
  else {
    const primary = (cfg.timelines || []).find(t => t.role === 'primary') || (cfg.timelines || [])[0];
    if (!primary) throw new Error(`${cfgPath}: no timelines listed`);
    timelines = [primary];
  }

  const excluded = ['feedback', 'grain', 'particles'].filter(t => m.exclude[t]);
  const exe = nativeExe();

  console.log('');
  console.log(`  parity gate — mode ${opt.mode.toUpperCase()} (${m.name || opt.mode})`);
  console.log(`  config:    ${cfgPath}`);
  console.log(`  excluding: ${excluded.join(' + ') || 'nothing'}`);
  console.log(`  geometry:  ${cfg.dump.width}x${cfg.dump.height} @ ${cfg.dump.renderScale}, stride ${cfg.dump.stride}`);
  console.log(`  native:    ${exe.replace(REPO + '\\', '')}`);
  console.log(`  timelines: ${timelines.map(t => t.path).join(', ')}`);
  if (m.isGate === false)
    console.log(`  NOTE: mode ${opt.mode} is informational — its verdict is a smoke check, not the gate.`);

  const failures = [];

  for (const tl of timelines) {
    const stem = tl.path.replace(/\\/g, '/').split('/').pop().replace(/\.json$/, '');
    const base = join(REPO, opt.out, `${opt.mode}-${stem}`);
    const dirH = join(base, 'harness');
    const dirN = join(base, 'native');
    const report = join(base, 'ssim-report.json');

    if (!opt.keep && !opt.skipHarness && !opt.skipNative && existsSync(base))
      rmSync(base, { recursive: true, force: true });
    mkdirSync(base, { recursive: true });

    const presetArgs = opt.preset ? ['--preset', opt.preset] : ['--all'];

    if (!opt.skipHarness) {
      const st = run(process.execPath, [
        'tools/parity-harness.mjs',
        '--timeline', tl.path, '--out', dirH,
        '--parity-mode', opt.mode, '--parity-config', opt.parityConfig,
        ...presetArgs,
      ], `harness dump — ${stem}`);
      if (st !== 0) { failures.push(`${stem}: harness dump failed`); continue; }
    }

    if (!opt.skipNative) {
      const st = run(exe, [
        '--dump', '--timeline', tl.path, '--out', dirN,
        '--parity-mode', opt.mode, '--parity-config', opt.parityConfig,
        ...presetArgs,
      ], `native dump — ${stem}`);
      if (st !== 0) { failures.push(`${stem}: native dump failed`); continue; }
    }

    const st = run(process.execPath, [
      'tools/compare-ssim.mjs', dirH, dirN,
      '--mode', opt.mode, '--parity-config', opt.parityConfig,
      '--out', report, '--quiet',
    ], `compare — ${stem}`);
    if (st !== 0) failures.push(`${stem}: SSIM gate failed (see ${report})`);
  }

  console.log('');
  if (failures.length) {
    console.log('  GATE FAIL');
    for (const f of failures) console.log(`    - ${f}`);
    console.log('');
    console.log('  Before changing anything: run tools/compare-trace.mjs on the two dumps.');
    console.log('  A host-side parameter divergence is far likelier than a shader one, and it');
    console.log('  is the only kind fixable without touching the shared shaders.');
    console.log('');
    return 1;
  }
  console.log(`  GATE PASS — mode ${opt.mode.toUpperCase()}, ${timelines.length} timeline(s)`);
  console.log('');
  return 0;
}

try { process.exit(main()); }
catch (e) { console.error(`parity-gate: ${e.stack || e.message}`); process.exit(2); }
