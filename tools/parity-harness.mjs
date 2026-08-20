#!/usr/bin/env node
/*
 * tools/parity-harness.mjs
 *
 * Drives harness/index.html headlessly so the Phase 1 parity gate can run from
 * a command line: record a Feature Frame timeline, and/or replay it in exact
 * step mode and dump PNG frames — the same thing the REC / STEP / DUMP buttons
 * do, without a human clicking them or picking a download directory.
 *
 * It talks to Chrome over the DevTools Protocol and calls into window.__ripple,
 * the automation surface at the bottom of harness/index.html. Every entry there
 * is a thin wrapper over the functions the panel buttons call, so what this
 * automates is the same code path a person exercises by hand.
 *
 * DEPENDENCY-FREE, same rule as the other tools here: CDP is HTTP for discovery
 * plus a WebSocket, and node has both built in. No puppeteer, no playwright.
 *
 * Usage:
 *   # record a 20 s timeline off the synthetic driver
 *   node tools/parity-harness.mjs --record captures/timeline-20s.json
 *
 *   # replay it and dump frames (mode A = the strict gate)
 *   node tools/parity-harness.mjs --timeline captures/timeline-20s.json \
 *        --out captures/harness-a --all --stride 20 --no-feedback
 *
 * Options:
 *   --record FILE     record a timeline and write it here
 *   --seconds N       recording length                     (default 20)
 *   --rate N          recording frame rate                 (default 60)
 *   --timeline FILE   timeline to replay
 *   --out DIR         dump frames here (one subdirectory per preset)
 *   --all             dump every preset in the library, not just one
 *   --preset ID       dump only this preset id or index
 *   --stride N        write every Nth frame                (default 10)
 *   --size WxH        drawing buffer                       (default 960x540)
 *   --no-feedback     gate mode A: clear the feedback chain every frame
 *   --no-particles    diagnostic: drop the particle layer
 *   --trace           also write trace.json (per-frame draw state)
 *   --chrome PATH     Chrome executable
 *   --headful         show the browser (debugging this script)
 *   --swiftshader     force ANGLE's software rasterizer
 *   --keep-open       leave Chrome running after the dump
 */
import { writeFileSync, mkdirSync, readFileSync, existsSync, rmSync, readdirSync } from 'node:fs';
import { spawn } from 'node:child_process';
import { join, resolve, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { pathToFileURL } from 'node:url';

/* ========================================================================== */
/*  CLI                                                                       */
/* ========================================================================== */

function parseArgs(argv) {
  const o = {
    record: null, seconds: 20, rate: 60,
    timeline: null, out: null, all: false, preset: null, stride: 10,
    width: 960, height: 540, noFeedback: false, noParticles: false, trace: false,
    chrome: null, headful: false, swiftshader: false, keepOpen: false, help: false,
  };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => {
      if (i + 1 >= argv.length) throw new Error(`${a} needs a value`);
      return argv[++i];
    };
    switch (a) {
      case '--record': o.record = next(); break;
      case '--seconds': o.seconds = Number(next()); break;
      case '--rate': o.rate = Number(next()); break;
      case '--timeline': o.timeline = next(); break;
      case '--out': o.out = next(); break;
      case '--all': o.all = true; break;
      case '--preset': o.preset = next(); break;
      case '--stride': o.stride = Number(next()); break;
      case '--no-feedback': o.noFeedback = true; break;
      case '--no-particles': o.noParticles = true; break;
      case '--trace': o.trace = true; break;
      case '--chrome': o.chrome = next(); break;
      case '--headful': o.headful = true; break;
      case '--swiftshader': o.swiftshader = true; break;
      case '--keep-open': o.keepOpen = true; break;
      case '-h': case '--help': o.help = true; break;
      case '--size': {
        const m = /^(\d+)[xX](\d+)$/.exec(next());
        if (!m) throw new Error('--size wants WxH, e.g. 960x540');
        o.width = +m[1]; o.height = +m[2];
        break;
      }
      default: throw new Error(`unknown option: ${a}`);
    }
  }
  if (!o.help && !o.record && !o.out)
    throw new Error('nothing to do: pass --record FILE and/or --out DIR');
  if (o.out && !o.timeline && !o.record)
    throw new Error('--out needs a timeline: pass --timeline FILE or --record FILE');
  return o;
}

const CHROME_CANDIDATES = [
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
];

function findChrome(explicit) {
  if (explicit) {
    if (!existsSync(explicit)) throw new Error(`no such browser: ${explicit}`);
    return explicit;
  }
  for (const c of CHROME_CANDIDATES) if (existsSync(c)) return c;
  throw new Error('no Chrome or Edge found; pass --chrome PATH');
}

const sleep = ms => new Promise(r => setTimeout(r, ms));

/* ========================================================================== */
/*  Chrome + CDP                                                              */
/* ========================================================================== */

class Browser {
  constructor(proc, ws, profileDir) {
    this.proc = proc;
    this.ws = ws;
    this.profileDir = profileDir;
    this.nextId = 1;
    this.pending = new Map();
    ws.addEventListener('message', ev => {
      const msg = JSON.parse(ev.data);
      if (msg.id && this.pending.has(msg.id)) {
        const { resolve: res, reject } = this.pending.get(msg.id);
        this.pending.delete(msg.id);
        if (msg.error) reject(new Error(`${msg.error.message} (${msg.error.code})`));
        else res(msg.result);
      }
    });
  }

  send(method, params = {}) {
    const id = this.nextId++;
    return new Promise((res, reject) => {
      this.pending.set(id, { resolve: res, reject });
      this.ws.send(JSON.stringify({ id, method, params }));
    });
  }

  /**
   * Evaluate in the page and return the value. Page exceptions are rethrown
   * here rather than surfacing as an undefined result three steps later.
   */
  async eval(expression) {
    const r = await this.send('Runtime.evaluate', {
      expression, awaitPromise: true, returnByValue: true,
    });
    if (r.exceptionDetails) {
      const e = r.exceptionDetails;
      throw new Error('page threw: ' + (e.exception?.description || e.text));
    }
    return r.result.value;
  }

  async close(keepOpen) {
    if (keepOpen) return;
    try { await this.send('Browser.close'); } catch { /* already gone */ }
    this.ws.close();
    await sleep(150);
    try { this.proc.kill(); } catch { /* already gone */ }
    try { rmSync(this.profileDir, { recursive: true, force: true }); } catch { /* leave it */ }
  }
}

async function launch(opt) {
  const exe = findChrome(opt.chrome);
  const profileDir = join(tmpdir(), `ripple-parity-${process.pid}-${Date.now()}`);
  mkdirSync(profileDir, { recursive: true });

  const args = [
    '--remote-debugging-port=0',
    `--user-data-dir=${profileDir}`,
    '--no-first-run',
    '--no-default-browser-check',
    '--disable-extensions',
    '--disable-background-timer-throttling',
    // The dump loop is driven from node, but the harness still needs rAF to
    // fire at least once to finish its own init; a backgrounded window in
    // headless mode otherwise never gets there.
    '--disable-backgrounding-occluded-windows',
    '--disable-renderer-backgrounding',
    '--allow-file-access-from-files',
    `--window-size=${Math.max(opt.width, 1280)},${Math.max(opt.height + 200, 800)}`,
  ];
  if (!opt.headful) args.push('--headless=new');
  if (opt.swiftshader) args.push('--use-angle=swiftshader', '--enable-unsafe-swiftshader');

  const proc = spawn(exe, args, { stdio: ['ignore', 'pipe', 'pipe'] });
  proc.stderr.on('data', () => {});   // Chrome is chatty on stderr; ignore it

  // --remote-debugging-port=0 picks a free port and writes it here.
  const portFile = join(profileDir, 'DevToolsActivePort');
  let port = null;
  for (let i = 0; i < 200 && port === null; i++) {
    await sleep(50);
    if (!existsSync(portFile)) continue;
    const first = readFileSync(portFile, 'utf8').split('\n')[0].trim();
    if (first) port = Number(first);
  }
  if (port === null) { try { proc.kill(); } catch {} throw new Error('Chrome never reported a debugging port'); }

  // Attach to the tab Chrome opened for us rather than creating one: a new
  // target in headless mode can start out throttled.
  let target = null;
  for (let i = 0; i < 100 && !target; i++) {
    try {
      const list = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
      target = list.find(t => t.type === 'page');
    } catch { /* not up yet */ }
    if (!target) await sleep(50);
  }
  if (!target) { try { proc.kill(); } catch {} throw new Error('no page target'); }

  const ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((res, rej) => {
    ws.addEventListener('open', res, { once: true });
    ws.addEventListener('error', () => rej(new Error('CDP websocket failed')), { once: true });
  });

  const b = new Browser(proc, ws, profileDir);
  await b.send('Runtime.enable');
  await b.send('Page.enable');
  return b;
}

/**
 * Load the harness and wait for the automation surface. A WebGL failure shows
 * up as a page that never defines window.__ripple, so time out with the
 * harness's own fatal message rather than hanging.
 */
async function openHarness(b, htmlPath) {
  const url = pathToFileURL(htmlPath).href;
  await b.send('Page.navigate', { url });
  for (let i = 0; i < 300; i++) {
    await sleep(100);
    const ready = await b.eval('!!(window.__ripple && window.__ripple.ready)');
    if (ready) return;
    // The harness routes every unrecoverable error through fatal(), which
    // unhides #glerr. Check that element rather than scraping body text: the
    // control panel's own copy mentions WebGL2 in passing.
    const fatal = await b.eval(
      `(() => { const e = document.getElementById('glerr');
                return (e && getComputedStyle(e).display !== 'none')
                  ? (e.textContent || 'unknown') : ''; })()`);
    if (fatal) throw new Error(`harness reported a fatal error:\n  ${fatal.trim()}`);
  }
  throw new Error('harness never exposed window.__ripple (30 s)');
}

/* ========================================================================== */
/*  the run                                                                   */
/* ========================================================================== */

async function main() {
  let opt;
  try { opt = parseArgs(process.argv.slice(2)); }
  catch (e) {
    console.error(`parity-harness: ${e.message}`);
    console.error('\nrun with --help for usage');
    return 2;
  }
  if (opt.help) {
    console.log(readFileSync(new URL(import.meta.url)).toString()
      .split('\n').slice(2, 46).map(l => l.replace(/^ \*ing?/, '').replace(/^ \* ?/, '')).join('\n'));
    return 0;
  }

  const repoRoot = resolve(dirname(new URL(import.meta.url).pathname.replace(/^\//, '')), '..');
  const htmlPath = join(repoRoot, 'harness', 'index.html');
  if (!existsSync(htmlPath)) throw new Error(`harness not found at ${htmlPath}`);

  console.log('[harness] launching ' + (opt.headful ? 'headful' : 'headless')
    + (opt.swiftshader ? ' (swiftshader)' : ''));
  const b = await launch(opt);

  try {
    await openHarness(b, htmlPath);
    const gl = await b.eval('window.__ripple.glInfo()');
    console.log(`[harness] ${gl.version}`);
    console.log(`[harness] ${gl.renderer}`);
    console.log(`[harness] colour buffer ${gl.colorBuffer}`
      + (gl.float ? '' : '  ** no float target: parity against RGBA16F will be poor **'));

    /* ---- record ---- */
    let timelinePath = opt.timeline ? resolve(opt.timeline) : null;
    if (opt.record) {
      console.log(`[record] ${opt.seconds}s @ ${opt.rate} fps off the synthetic driver`);
      const tl = await b.eval(
        `JSON.stringify(window.__ripple.record(${opt.seconds}, ${opt.rate}))`);
      const dest = resolve(opt.record);
      mkdirSync(dirname(dest), { recursive: true });
      writeFileSync(dest, tl);
      const parsed = JSON.parse(tl);
      console.log(`[record] ${parsed.frames.length} frames -> ${dest}`);
      if (!timelinePath) timelinePath = dest;
    }

    /* ---- dump ---- */
    if (opt.out) {
      const json = readFileSync(timelinePath, 'utf8');
      // The timeline crosses into the page as a JSON string literal and is
      // parsed there — embedding the object directly would put a 4 MB
      // expression through the CDP evaluate path for no reason.
      const n = await b.eval(`window.__ripple.load(JSON.parse(${JSON.stringify(json)}))`);
      console.log(`[dump] timeline ${timelinePath}: ${n} frames`);

      const presets = await b.eval('window.__ripple.presets()');
      let chosen;
      if (opt.all) chosen = presets;
      else if (opt.preset !== null) {
        const idx = Number(opt.preset);
        chosen = Number.isInteger(idx) && String(idx) === String(opt.preset)
          ? [presets[idx]]
          : presets.filter(p => p.id === opt.preset);
        if (!chosen.length || !chosen[0]) throw new Error(`no such preset: ${opt.preset}`);
      } else chosen = [presets[0]];

      const outRoot = resolve(opt.out);
      mkdirSync(outRoot, { recursive: true });
      console.log(`[dump] ${chosen.length} preset(s) x ${n} frames @ ${opt.width}x${opt.height}, `
        + `writing every ${opt.stride}, mode ${opt.noFeedback ? 'A (no feedback)' : 'B (full feedback)'}`);

      const started = Date.now();
      let written = 0;

      for (const p of chosen) {
        const info = await b.eval(`JSON.stringify(window.__ripple.parityBegin(${JSON.stringify({
          presetIdx: p.index, w: opt.width, h: opt.height,
          stride: opt.stride, noFeedback: opt.noFeedback, noParticles: opt.noParticles,
        })}))`).then(JSON.parse);

        const dir = join(outRoot, info.preset);
        mkdirSync(dir, { recursive: true });

        // Every frame is RENDERED; only the writing is strided. The feedback
        // buffer IS the history, and the motif envelopes, palette rotation and
        // safety loop all advance per frame, so skipping a render would change
        // every frame after it and the hosts would be comparing different runs.
        //
        // The frames between two captures are stepped in a single call: a CDP
        // round trip costs more than the frame does, and 1200 of them per
        // preset dominated the run before this was batched.
        // With --trace every frame needs its own round trip, because a row is
        // collected per frame rather than per capture.
        const traceRows = opt.trace ? [] : null;
        let rendered = 0;
        for (let c = 0; c < n; c += opt.stride) {
          if (traceRows) {
            for (let k = rendered; k <= c; k++) {
              await b.eval('window.__ripple.step(1)');
              traceRows.push(await b.eval('window.__ripple.trace()'));
            }
          } else {
            await b.eval(`window.__ripple.step(${c - rendered + 1})`);
          }
          rendered = c + 1;
          const b64 = await b.eval('window.__ripple.capture()');
          writeFileSync(join(dir, `frame_${String(c).padStart(5, '0')}.png`),
            Buffer.from(b64, 'base64'));
          written++;
          if (written % 10 === 0)
            process.stderr.write(`\r  ${info.preset}: ${rendered}/${n} frames`);
        }
        process.stderr.write('\r' + ' '.repeat(60) + '\r');

        const manifest = await b.eval(`JSON.stringify(window.__ripple.manifest(${JSON.stringify({
          timeline: timelinePath.replace(/\\/g, '/').split('/').pop(),
        })}))`);
        writeFileSync(join(dir, 'manifest.json'),
          JSON.stringify(JSON.parse(manifest), null, 2));

        if (traceRows) {
          const keys = await b.eval('window.__ripple.traceKeys()');
          writeFileSync(join(dir, 'trace.json'), JSON.stringify(
            { kind: 'ripple-draw-trace', host: 'harness', preset: info.preset,
              keys, rows: traceRows }));
        }
        await b.eval('window.__ripple.parityEnd()');

        const count = readdirSync(dir).filter(f => f.endsWith('.png')).length;
        console.log(`[dump] ${info.preset.padEnd(18)} ${String(info.fractal).padEnd(12)} `
          + `${n} frames, ${count} png`);
      }

      console.log(`[dump] ${written} frames written to ${outRoot} `
        + `in ${((Date.now() - started) / 1000).toFixed(1)}s`);
    }
    return 0;
  } finally {
    await b.close(opt.keepOpen);
  }
}

main().then(c => process.exit(c)).catch(e => {
  console.error(`parity-harness: ${e.stack || e.message}`);
  process.exit(1);
});
