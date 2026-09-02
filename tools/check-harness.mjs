#!/usr/bin/env node
/*
 * tools/check-harness.mjs
 *
 * Load harness/index.html in headless Chrome and fail on any page exception or console error.
 * Nothing is rendered and nothing is compared — this answers one question in a few seconds:
 * **does the harness still run at all?**
 *
 * WHY IT EXISTS. The harness is one large HTML file holding the shader sources as template
 * literals and the era presets as embedded JSON, and it has now been broken twice by careless
 * text edits, each time in a way that no static check caught:
 *
 *   - a BACKTICK inside a shader comment terminated the template literal, so the whole script
 *     block failed to parse and the symptom was `ReferenceError: gl is not defined` thrown
 *     from a completely unrelated block;
 *   - a shell quoting accident turned `key === 'barPhase'` into `key === barPhase`, which
 *     parses perfectly and throws `ReferenceError: barPhase is not defined` only when a preset
 *     actually routes that source.
 *
 * Both were found by the parity gate, which takes about twenty minutes per timeline and
 * reports "harness dump failed". A syntax error deserves a faster answer than that.
 *
 * It also exercises the mapping: every source named in any preset is pushed through
 * `rhythmicShape`, which is what turns the second bug above from "throws in some presets" into
 * "throws here, now".
 *
 * Usage:
 *   node tools/check-harness.mjs [--chrome PATH] [--headful]
 */
import { spawn } from 'node:child_process';
import { existsSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const argv = process.argv.slice(2);
const arg = (n) => { const i = argv.indexOf('--' + n); return i === -1 ? null : argv[i + 1]; };
const headful = argv.includes('--headful');

const CHROME_CANDIDATES = [
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
];

function findChrome(explicit) {
  if (explicit) return explicit;
  for (const c of CHROME_CANDIDATES) if (existsSync(c)) return c;
  throw new Error('no Chrome or Edge found; pass --chrome PATH');
}

const sleep = (ms) => new Promise(r => setTimeout(r, ms));

const harnessUrl = () =>
  'file:///' + join(REPO, 'harness', 'index.html').replace(/\\/g, '/');

async function main() {
  const exe = findChrome(arg('chrome'));
  const profileDir = join(tmpdir(), `ripple-harness-check-${process.pid}`);
  const port = 9333 + (process.pid % 500);

  const args = [
    `--remote-debugging-port=${port}`,
    `--user-data-dir=${profileDir}`,
    '--no-first-run', '--no-default-browser-check',
    '--disable-extensions', '--disable-background-networking',
    'about:blank',
  ];
  if (!headful) args.push('--headless=new');

  const proc = spawn(exe, args, { stdio: 'ignore' });

  // Wait for the debugger, then attach to the page target.
  let target = null;
  for (let i = 0; i < 100 && !target; i++) {
    await sleep(100);
    try {
      const r = await fetch(`http://127.0.0.1:${port}/json/list`);
      const list = await r.json();
      target = list.find(t => t.type === 'page' && t.webSocketDebuggerUrl);
    } catch { /* not up yet */ }
  }
  if (!target) { proc.kill(); throw new Error('Chrome did not expose a debug target'); }

  const ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((res, rej) => {
    ws.addEventListener('open', res, { once: true });
    ws.addEventListener('error', () => rej(new Error('CDP websocket failed')), { once: true });
  });

  const problems = [];
  let nextId = 1;
  const pending = new Map();

  ws.addEventListener('message', (ev) => {
    const msg = JSON.parse(ev.data);
    if (msg.id && pending.has(msg.id)) {
      const { res, rej } = pending.get(msg.id);
      pending.delete(msg.id);
      msg.error ? rej(new Error(msg.error.message)) : res(msg.result);
      return;
    }
    if (msg.method === 'Runtime.exceptionThrown') {
      const e = msg.params.exceptionDetails;
      problems.push('uncaught: ' + (e.exception?.description || e.text));
    }
    if (msg.method === 'Runtime.consoleAPICalled' && msg.params.type === 'error') {
      problems.push('console.error: '
        + msg.params.args.map(a => a.value ?? a.description ?? '?').join(' '));
    }
  });

  const send = (method, params = {}) => new Promise((res, rej) => {
    const id = nextId++;
    pending.set(id, { res, rej });
    ws.send(JSON.stringify({ id, method, params }));
  });

  const evaluate = async (expression) => {
    const r = await send('Runtime.evaluate',
      { expression, awaitPromise: true, returnByValue: true });
    if (r.exceptionDetails) {
      throw new Error('page threw: '
        + (r.exceptionDetails.exception?.description || r.exceptionDetails.text));
    }
    return r.result.value;
  };

  await send('Runtime.enable');
  await send('Page.enable');

  // Navigate AFTER attaching, not by passing the URL on the command line. Chrome boots the
  // page before the debugger connects, so a URL on the command line means every exception
  // thrown during load happens before Runtime.enable and is never reported — which is exactly
  // the class of failure this tool exists to catch. It was caught here first: the page came
  // back with window.__ripple undefined and not one exception logged.
  await send('Page.navigate', { url: harnessUrl() });

  // Poll for readiness rather than sleeping a fixed amount. A fixed wait is a race: it was
  // 2.5 s and still lost often enough to report "LIBRARY is not defined" on a harness that
  // was completely fine. Waiting on the thing itself is both faster and not flaky.
  for (let i = 0; i < 120; i++) {
    await sleep(100);
    const ready = await evaluate(
      "(typeof window.__ripple === 'object' && typeof LIBRARY !== 'undefined') "
      + "|| document.readyState === 'complete'").catch(() => false);
    if (ready) break;
  }
  await sleep(400);           // one more frame, so a render-time throw is observed too

  // The automation surface is the contract the parity harness drives; if it is missing,
  // something failed to parse long before any preset was applied.
  const surface = await evaluate('typeof window.__ripple');
  if (surface !== 'object') problems.push(`window.__ripple is ${surface}, expected object`);

  // Exercise every mapping source every preset names, through the real shaping function.
  // This is what makes an unquoted identifier fail HERE rather than in the parity gate.
  const routed = await evaluate(`(() => {
    const seen = new Set();
    for (const p of LIBRARY) for (const src in (p.mapping || {})) seen.add(src);
    const out = [];
    for (const src of seen) {
      try { rhythmicShape(src, 0.5); out.push(src); }
      catch (e) { out.push(src + ' !! ' + e.message); }
    }
    return out;
  })()`);

  const broken = routed.filter(r => r.includes('!!'));
  for (const b of broken) problems.push('rhythmicShape: ' + b);

  console.log(`  ${routed.length} mapping source(s) exercised through rhythmicShape`);
  console.log(`  window.__ripple: ${surface}`);

  try { await send('Browser.close'); } catch { /* already gone */ }
  ws.close();
  await sleep(150);
  try { proc.kill(); } catch { /* already gone */ }
  try { rmSync(profileDir, { recursive: true, force: true }); } catch { /* leave it */ }

  if (problems.length === 0) {
    console.log('\n  harness loads clean');
    process.exit(0);
  }
  for (const p of problems) console.log(`  FAIL  ${p}`);
  console.log(`\n  ${problems.length} problem(s) — the parity gate would fail on this`);
  process.exit(1);
}

main().catch(e => { console.error('check-harness: ' + e.message); process.exit(2); });
