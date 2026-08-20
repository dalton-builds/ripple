#!/usr/bin/env node
/*
 * tools/compare-ssim.mjs
 *
 * The Phase 1 cross-host parity gate: diff two directories of PNG frames, one
 * dumped by the WebGL2 harness and one by the native host, and report SSIM.
 *
 * ---------------------------------------------------------------------------
 *  WHY THERE ARE TWO MODES
 * ---------------------------------------------------------------------------
 * Ripple is a FEEDBACK renderer. Every frame warps the previous frame's buffer,
 * so a difference of one ulp between two GL implementations does not stay one
 * ulp — it is fed back in, amplified by the warp, and compounds. Two hosts
 * running byte-identical shaders on byte-identical inputs will still drift to
 * visibly different images over a 20-second run. That drift is the feedback
 * loop working as designed, not a violated contract.
 *
 * So a naive "SSIM >= 0.99 on every frame" gate fails on CORRECT code, and the
 * tempting response to that failure is to start forking shaders — the one thing
 * the plan says must not happen. Hence:
 *
 *   MODE A (--mode a, the default; the real gate)
 *     Both hosts ran with the feedback carry disabled (native --no-feedback,
 *     harness MODE A), so each frame is computed fresh from its Feature Frame
 *     and nothing accumulates. This tests shader math directly. Every frame
 *     must clear --threshold (default 0.995). A failure here is a genuine
 *     contract bug: diagnose it, do not threshold it away.
 *
 *   MODE B (--mode b, informational)
 *     Full feedback. The SSIM curve is expected to decay monotonically. The
 *     only assertion is a smoke check at --smoke-frame (default 60): if the
 *     curve falls off a cliff instead of drifting, something is broken and the
 *     expected divergence is hiding it.
 *
 * ---------------------------------------------------------------------------
 *  DEPENDENCIES: none, deliberately
 * ---------------------------------------------------------------------------
 * Same rule as tools/extract-shaders.mjs. PNG decode is node's zlib plus a
 * scanline unfilter; the encoder is the minimum needed to write diff images.
 * Supports 8-bit non-interlaced greyscale/RGB/RGBA — which is everything either
 * host emits.
 *
 * Usage:
 *   node tools/compare-ssim.mjs <dirA> <dirB> [options]
 *
 *   --threshold N     per-frame SSIM floor            (default 0.995)
 *   --mode a|b        gate mode                       (default a)
 *   --smoke-frame N   mode B: the frame that must hold (default 60)
 *   --smoke N         mode B: the floor at that frame  (default 0.95)
 *   --out FILE        JSON report path        (default <dirA>/../ssim-report.json)
 *   --amplify N       diff-image gain                 (default 8)
 *   --no-diff         skip writing diff images
 *   --quiet           table + verdict only
 *
 * Both directories may either hold frame PNGs directly, or hold one
 * subdirectory per preset (which is what both hosts' batch dumps produce).
 * Frames are paired by frame index parsed out of the filename.
 *
 * Exit code is 0 only if the gate passes.
 */
import { readdirSync, readFileSync, writeFileSync, statSync, existsSync, mkdirSync } from 'node:fs';
import { inflateSync, deflateSync } from 'node:zlib';
import { join, dirname, basename, resolve } from 'node:path';
import { loadParityConfig, parityMode } from './parity-config.mjs';

/* ========================================================================== */
/*  PNG                                                                       */
/* ========================================================================== */

const PNG_SIG = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

const CRC_TABLE = (() => {
  const t = new Int32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = (c & 1) ? (0xedb88320 ^ (c >>> 1)) : (c >>> 1);
    t[n] = c;
  }
  return t;
})();

function crc32(buf) {
  let c = -1;
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ -1) >>> 0;
}

/* Channels per pixel by PNG colour type. Type 3 (palette) is rejected: neither
   host writes one, and quietly guessing at a palette would be worse than
   failing. */
const CHANNELS = { 0: 1, 2: 3, 4: 2, 6: 4 };

/**
 * Decode an 8-bit non-interlaced PNG to { width, height, channels, data }.
 * data is tightly packed, `channels` bytes per pixel, top-down.
 */
function decodePng(buf, label) {
  const fail = m => { throw new Error(`${label}: ${m}`); };
  if (buf.length < 8 || !buf.subarray(0, 8).equals(PNG_SIG)) fail('not a PNG');

  let off = 8;
  let ihdr = null;
  const idat = [];

  while (off + 8 <= buf.length) {
    const len = buf.readUInt32BE(off);
    const type = buf.toString('latin1', off + 4, off + 8);
    const body = buf.subarray(off + 8, off + 8 + len);
    if (type === 'IHDR') {
      ihdr = {
        width: body.readUInt32BE(0),
        height: body.readUInt32BE(4),
        depth: body[8],
        colorType: body[9],
        interlace: body[12],
      };
    } else if (type === 'IDAT') {
      idat.push(Buffer.from(body));
    } else if (type === 'IEND') {
      break;
    }
    off += 12 + len;            // length + type + data + crc
  }

  if (!ihdr) fail('no IHDR');
  if (ihdr.depth !== 8) fail(`bit depth ${ihdr.depth}, only 8 is supported`);
  if (ihdr.interlace !== 0) fail('interlaced PNGs are not supported');
  if (!(ihdr.colorType in CHANNELS)) fail(`colour type ${ihdr.colorType} is not supported`);
  if (!idat.length) fail('no IDAT');

  const ch = CHANNELS[ihdr.colorType];
  const w = ihdr.width, h = ihdr.height;
  const raw = inflateSync(Buffer.concat(idat));
  const stride = w * ch;
  if (raw.length < (stride + 1) * h)
    fail(`short IDAT: ${raw.length} bytes for ${h} rows of ${stride}`);

  const out = Buffer.allocUnsafe(stride * h);

  // Scanline filters, PNG spec 9.2. prev is the already-reconstructed row
  // above, which is why this has to run top-down and in place.
  for (let y = 0; y < h; y++) {
    const ft = raw[y * (stride + 1)];
    const src = raw.subarray(y * (stride + 1) + 1, y * (stride + 1) + 1 + stride);
    const cur = out.subarray(y * stride, (y + 1) * stride);
    const prev = y > 0 ? out.subarray((y - 1) * stride, y * stride) : null;

    switch (ft) {
      case 0:
        src.copy(cur);
        break;
      case 1:                                            // Sub
        for (let i = 0; i < stride; i++)
          cur[i] = (src[i] + (i >= ch ? cur[i - ch] : 0)) & 0xff;
        break;
      case 2:                                            // Up
        for (let i = 0; i < stride; i++)
          cur[i] = (src[i] + (prev ? prev[i] : 0)) & 0xff;
        break;
      case 3:                                            // Average
        for (let i = 0; i < stride; i++) {
          const a = i >= ch ? cur[i - ch] : 0;
          const b = prev ? prev[i] : 0;
          cur[i] = (src[i] + ((a + b) >> 1)) & 0xff;
        }
        break;
      case 4:                                            // Paeth
        for (let i = 0; i < stride; i++) {
          const a = i >= ch ? cur[i - ch] : 0;
          const b = prev ? prev[i] : 0;
          const c = (prev && i >= ch) ? prev[i - ch] : 0;
          const p = a + b - c;
          const pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c);
          const pred = (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
          cur[i] = (src[i] + pred) & 0xff;
        }
        break;
      default:
        fail(`unknown scanline filter ${ft} on row ${y}`);
    }
  }

  return { width: w, height: h, channels: ch, data: out };
}

/** Minimal encoder: 8-bit RGB, filter 0, one IDAT. Enough for diff images. */
function encodePngRgb(rgb, w, h) {
  const stride = w * 3;
  const raw = Buffer.allocUnsafe((stride + 1) * h);
  for (let y = 0; y < h; y++) {
    raw[y * (stride + 1)] = 0;
    rgb.copy(raw, y * (stride + 1) + 1, y * stride, (y + 1) * stride);
  }

  const chunk = (type, body) => {
    const out = Buffer.allocUnsafe(12 + body.length);
    out.writeUInt32BE(body.length, 0);
    out.write(type, 4, 'latin1');
    body.copy(out, 8);
    out.writeUInt32BE(crc32(out.subarray(4, 8 + body.length)), 8 + body.length);
    return out;
  };

  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0);
  ihdr.writeUInt32BE(h, 4);
  ihdr[8] = 8;       // bit depth
  ihdr[9] = 2;       // colour type: RGB
  // compression 0, filter 0, interlace 0 — already zeroed.

  return Buffer.concat([
    PNG_SIG,
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw, { level: 6 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

/* ========================================================================== */
/*  SSIM                                                                      */
/* ========================================================================== */

/** Rec.709 luma, the same weights the composite pass uses for its safety loop. */
function toLuma(img) {
  const { width: w, height: h, channels: ch, data } = img;
  const out = new Float32Array(w * h);
  if (ch === 1 || ch === 2) {
    for (let i = 0, p = 0; i < out.length; i++, p += ch) out[i] = data[p];
    return out;
  }
  for (let i = 0, p = 0; i < out.length; i++, p += ch)
    out[i] = 0.2126 * data[p] + 0.7152 * data[p + 1] + 0.0722 * data[p + 2];
  return out;
}

const WIN = 11;
const SIGMA = 1.5;
const RADIUS = (WIN - 1) / 2;

const KERNEL = (() => {
  const k = new Float32Array(WIN);
  let sum = 0;
  for (let i = 0; i < WIN; i++) {
    const d = i - RADIUS;
    k[i] = Math.exp(-(d * d) / (2 * SIGMA * SIGMA));
    sum += k[i];
  }
  for (let i = 0; i < WIN; i++) k[i] /= sum;
  return k;
})();

/**
 * Separable Gaussian blur with edge clamping. Separable because the 2D form
 * costs 121 taps per pixel and this runs over a thousand frame pairs: 11 + 11
 * gives the identical result for a Gaussian at a fraction of the work.
 *
 * Clamping vs. reflecting vs. zero-padding does not affect the score: the mean
 * is taken over the valid interior only (every pixel at least RADIUS from an
 * edge), where no window ever reaches the padding. Full-size maps just keep the
 * diff-image indexing simple.
 */
function blur(src, w, h, scratch, dst) {
  for (let y = 0; y < h; y++) {
    const row = y * w;
    for (let x = 0; x < w; x++) {
      let acc = 0;
      for (let t = 0; t < WIN; t++) {
        let sx = x + t - RADIUS;
        if (sx < 0) sx = 0; else if (sx >= w) sx = w - 1;
        acc += KERNEL[t] * src[row + sx];
      }
      scratch[row + x] = acc;
    }
  }
  for (let x = 0; x < w; x++) {
    for (let y = 0; y < h; y++) {
      let acc = 0;
      for (let t = 0; t < WIN; t++) {
        let sy = y + t - RADIUS;
        if (sy < 0) sy = 0; else if (sy >= h) sy = h - 1;
        acc += KERNEL[t] * scratch[sy * w + x];
      }
      dst[y * w + x] = acc;
    }
  }
  return dst;
}

const C1 = (0.01 * 255) ** 2;
const C2 = (0.03 * 255) ** 2;

/**
 * Mean SSIM over the valid interior, Gaussian-weighted local statistics,
 * Wang et al. 2004. Also returns the worst single-pixel score, which is a
 * useful tell: a high mean with a very low minimum means the two renders agree
 * everywhere except one localized feature.
 */
function ssim(a, b, w, h) {
  const n = w * h;
  const sq = new Float32Array(n);
  const scratch = new Float32Array(n);

  const muA = blur(a, w, h, scratch, new Float32Array(n));
  const muB = blur(b, w, h, scratch, new Float32Array(n));

  for (let i = 0; i < n; i++) sq[i] = a[i] * a[i];
  const aa = blur(sq, w, h, scratch, new Float32Array(n));
  for (let i = 0; i < n; i++) sq[i] = b[i] * b[i];
  const bb = blur(sq, w, h, scratch, new Float32Array(n));
  for (let i = 0; i < n; i++) sq[i] = a[i] * b[i];
  const ab = blur(sq, w, h, scratch, new Float32Array(n));

  let sum = 0, count = 0, min = Infinity;
  for (let y = RADIUS; y < h - RADIUS; y++) {
    for (let x = RADIUS; x < w - RADIUS; x++) {
      const i = y * w + x;
      const ma = muA[i], mb = muB[i];
      const va = aa[i] - ma * ma;
      const vb = bb[i] - mb * mb;
      const cov = ab[i] - ma * mb;
      const v = ((2 * ma * mb + C1) * (2 * cov + C2)) /
                ((ma * ma + mb * mb + C1) * (va + vb + C2));
      sum += v; count++;
      if (v < min) min = v;
    }
  }
  return { mean: count ? sum / count : 1, minPixel: count ? min : 1 };
}

/** Amplified absolute difference, per channel, as an RGB buffer. */
function diffImage(A, B, amplify) {
  const w = A.width, h = A.height;
  const out = Buffer.alloc(w * h * 3);
  const ca = A.channels, cb = B.channels;
  for (let i = 0, o = 0; i < w * h; i++, o += 3) {
    for (let c = 0; c < 3; c++) {
      const va = A.data[i * ca + Math.min(c, ca - 1)];
      const vb = B.data[i * cb + Math.min(c, cb - 1)];
      out[o + c] = Math.min(255, Math.abs(va - vb) * amplify);
    }
  }
  return out;
}

/* ========================================================================== */
/*  frame pairing                                                             */
/* ========================================================================== */

/* frame_00000.png is what both hosts write now. The bare f00000.png form is
   still accepted so an older dump lying around does not read as "no frames". */
const FRAME_RE = /^(?:frame_|f)(\d+)\.png$/i;

function framesIn(dir) {
  return readdirSync(dir)
    .map(name => ({ name, m: FRAME_RE.exec(name) }))
    .filter(e => e.m)
    .map(e => ({ name: e.name, index: parseInt(e.m[1], 10) }))
    .sort((p, q) => p.index - q.index);
}

function readManifest(dir) {
  const p = join(dir, 'manifest.json');
  if (!existsSync(p)) return null;
  try { return JSON.parse(readFileSync(p, 'utf8')); } catch { return null; }
}

/**
 * A "group" is one preset. Both hosts dump <root>/<presetId>/frame_NNNNN.png,
 * but a root holding frames directly is accepted too, so a single-preset run
 * can be compared without inventing a directory level.
 */
function groupsIn(root) {
  if (framesIn(root).length) return [{ id: basename(root), dir: root }];
  return readdirSync(root)
    .filter(n => statSync(join(root, n)).isDirectory())
    .sort()
    .map(id => ({ id, dir: join(root, id) }));
}

/* ========================================================================== */
/*  CLI                                                                       */
/* ========================================================================== */

function parseArgs(argv) {
  const o = {
    dirs: [], threshold: 0.995, mode: 'a', smokeFrame: 60, smoke: 0.95,
    out: null, amplify: 8, diff: true, quiet: false, help: false,
    parityConfig: 'tests/parity-config.json',
    thresholdSpecified: false, smokeSpecified: false, smokeFrameSpecified: false,
  };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => {
      if (i + 1 >= argv.length) throw new Error(`${a} needs a value`);
      return argv[++i];
    };
    switch (a) {
      case '--threshold': o.threshold = Number(next()); o.thresholdSpecified = true; break;
      case '--parity-config': o.parityConfig = next(); break;
      case '--mode': o.mode = next().toLowerCase(); break;
      case '--smoke-frame': o.smokeFrame = Number(next()); o.smokeFrameSpecified = true; break;
      case '--smoke': o.smoke = Number(next()); o.smokeSpecified = true; break;
      case '--out': o.out = next(); break;
      case '--amplify': o.amplify = Number(next()); break;
      case '--no-diff': o.diff = false; break;
      case '--quiet': o.quiet = true; break;
      case '-h': case '--help': o.help = true; break;
      default:
        if (a.startsWith('--')) throw new Error(`unknown option: ${a}`);
        o.dirs.push(a);
    }
  }
  if (o.help) return o;
  if (o.dirs.length !== 2) throw new Error('need exactly two directories: <dirA> <dirB>');
  if (!(o.threshold >= 0 && o.threshold <= 1)) throw new Error('--threshold must be 0..1');

  // Thresholds and the expected exclusion set come from the config, so this tool and
  // the two hosts cannot disagree about what a mode means. A flag still overrides.
  const cfgPath = resolve(o.parityConfig);
  if (existsSync(cfgPath)) {
    const cfg = loadParityConfig(cfgPath);
    const m = parityMode(cfg, o.mode, cfgPath);
    o.expect = m.exclude;
    o.modeName = m.name || o.mode;
    o.isGate = m.isGate !== false;
    const g = m.gate || {};
    if (g.type === 'per-frame' && !o.thresholdSpecified && typeof g.threshold === 'number')
      o.threshold = g.threshold;
    if (g.type === 'smoke') {
      if (!o.smokeSpecified && typeof g.threshold === 'number') o.smoke = g.threshold;
      if (!o.smokeFrameSpecified && typeof g.frame === 'number') o.smokeFrame = g.frame;
    }
    o.gateType = g.type || (o.mode === 'a' ? 'per-frame' : 'smoke');
  } else {
    if (o.mode !== 'a' && o.mode !== 'b')
      throw new Error(`--mode must be a or b without a config, got ${o.mode}`);
    o.gateType = o.mode === 'a' ? 'per-frame' : 'smoke';
    o.expect = o.mode === 'a'
      ? { feedback: true, grain: true, particles: true }
      : { feedback: false, grain: false, particles: false };
    o.modeName = o.mode;
    o.isGate = o.mode === 'a';
  }
  return o;
}

const USAGE = `
compare-ssim.mjs <dirA> <dirB> [options]

  --threshold N     per-frame SSIM floor (mode A gate)   default 0.995
  --mode a|b        a = strict gate, b = informational    default a
  --smoke-frame N   mode B: frame that must hold          default 60
  --smoke N         mode B: floor at that frame           default 0.95
  --out FILE        JSON report path
  --amplify N       diff-image gain                       default 8
  --no-diff         do not write diff images
  --quiet           table and verdict only

Mode A expects both dumps to have run with the feedback carry disabled
(native --no-feedback, harness MODE A). Mode B is full feedback, where
cross-host divergence compounds by design and the curve is informational.
`.trim();

const pad = (s, n, right = false) =>
  right ? String(s).padEnd(n) : String(s).padStart(n);
const f4 = v => (v === null || v === undefined || Number.isNaN(v)) ? '  --  ' : v.toFixed(4);

function main() {
  let opt;
  try {
    opt = parseArgs(process.argv.slice(2));
  } catch (e) {
    console.error(`compare-ssim: ${e.message}\n`);
    console.error(USAGE);
    return 2;
  }
  if (opt.help) { console.log(USAGE); return 0; }

  const [rootA, rootB] = opt.dirs.map(d => resolve(d));
  for (const d of [rootA, rootB]) {
    if (!existsSync(d)) { console.error(`compare-ssim: no such directory: ${d}`); return 2; }
  }

  const reportPath = resolve(opt.out || join(dirname(rootA), 'ssim-report.json'));
  const diffDir = join(dirname(reportPath), 'diff');

  const groups = groupsIn(rootA);
  if (!groups.length) { console.error(`compare-ssim: no frames under ${rootA}`); return 2; }

  const warnings = [];
  const results = [];

  for (const g of groups) {
    const dirA = g.dir;
    const dirB = (groups.length === 1 && framesIn(rootB).length) ? rootB : join(rootB, g.id);
    if (!existsSync(dirB)) {
      warnings.push(`${g.id}: missing in B (${dirB})`);
      results.push({ preset: g.id, error: 'missing in B', frames: [] });
      continue;
    }

    // Manifest cross-checks. These catch the failure that would otherwise look
    // like a shader bug: two dumps that were never comparable to begin with.
    const mA = readManifest(dirA), mB = readManifest(dirB);
    for (const [k, label] of [['width', 'width'], ['height', 'height'],
                              ['rate', 'rate'], ['stride', 'stride'],
                              ['gateMode', 'gate mode'], ['frames', 'timeline length'],
                              ['rngParticleSeed', 'particle RNG seed'],
                              ['rngMotifSeed', 'motif RNG seed']]) {
      if (mA && mB && mA[k] !== undefined && mB[k] !== undefined && mA[k] !== mB[k])
        warnings.push(`${g.id}: ${label} differs — A ${mA[k]} vs B ${mB[k]}`);
    }
    // A dump that ran with the wrong exclusions is the failure that looks most like
    // a shader bug, so say so loudly rather than grading it.
    for (const [m, side] of [[mA, 'A'], [mB, 'B']]) {
      if (!m) continue;
      for (const term of ['feedback', 'grain', 'particles']) {
        if (m[term] === undefined) continue;
        const rendered = m[term];               // manifest records what WAS rendered
        const shouldRender = !opt.expect[term];
        if (rendered !== shouldRender)
          warnings.push(`${g.id}: dump ${side} rendered ${term}=${rendered}, `
            + `but mode ${opt.mode} expects ${term}=${shouldRender}`);
      }
    }

    const listA = framesIn(dirA);
    const byB = new Map(framesIn(dirB).map(f => [f.index, f.name]));
    const frames = [];

    for (const fa of listA) {
      const nb = byB.get(fa.index);
      if (!nb) { warnings.push(`${g.id}: frame ${fa.index} missing in B`); continue; }

      const A = decodePng(readFileSync(join(dirA, fa.name)), `${g.id}/A/${fa.name}`);
      const B = decodePng(readFileSync(join(dirB, nb)), `${g.id}/B/${nb}`);
      if (A.width !== B.width || A.height !== B.height) {
        warnings.push(`${g.id}: frame ${fa.index} is ${A.width}x${A.height} in A `
          + `but ${B.width}x${B.height} in B — skipped`);
        continue;
      }

      const s = ssim(toLuma(A), toLuma(B), A.width, A.height);
      const rec = { index: fa.index, file: fa.name, ssim: s.mean, minPixel: s.minPixel };
      frames.push(rec);

      // A scalar says two renders disagree; the diff image says WHERE, which is
      // what actually localizes the cause.
      if (opt.diff && s.mean < opt.threshold) {
        mkdirSync(diffDir, { recursive: true });
        const name = `diff-${g.id}-frame_${String(fa.index).padStart(5, '0')}.png`;
        writeFileSync(join(diffDir, name),
          encodePngRgb(diffImage(A, B, opt.amplify), A.width, A.height));
        rec.diff = `diff/${name}`;
      }
      if (!opt.quiet && frames.length % 10 === 0)
        process.stderr.write(`\r  ${g.id}: ${frames.length}/${listA.length} frames`);
    }
    if (!opt.quiet) process.stderr.write('\r' + ' '.repeat(60) + '\r');

    if (!frames.length) {
      results.push({ preset: g.id, error: 'no comparable frames', frames: [] });
      continue;
    }

    const mean = frames.reduce((s, f) => s + f.ssim, 0) / frames.length;
    let worst = frames[0];
    for (const f of frames) if (f.ssim < worst.ssim) worst = f;

    // The sampled frame nearest --smoke-frame; the dump is strided, so the
    // exact index may not have been written.
    const atSmoke = frames.reduce((best, f) =>
      (best === null || Math.abs(f.index - opt.smokeFrame) < Math.abs(best.index - opt.smokeFrame))
        ? f : best, null);

    const pass = opt.gateType === 'per-frame'
      ? worst.ssim >= opt.threshold
      : atSmoke.ssim >= opt.smoke;

    results.push({
      preset: g.id, count: frames.length, mean,
      min: worst.ssim, worstIndex: worst.index, worstFile: worst.file,
      first: frames[0].ssim, last: frames[frames.length - 1].ssim,
      smokeIndex: atSmoke.index, smokeSsim: atSmoke.ssim,
      below: frames.filter(f => f.ssim < opt.threshold).length,
      manifestA: mA, manifestB: mB, pass, frames,
    });
  }

  /* ---- console table ---- */
  const graded = results.filter(r => !r.error);
  const w0 = Math.max(8, ...results.map(r => r.preset.length));
  const header = opt.gateType === 'per-frame'
    ? `${pad('preset', w0, true)}  frames    mean       min   worst@  verdict`
    : `${pad('preset', w0, true)}  frames    mean   frame0   @${pad(opt.smokeFrame, 4, true)}    last  verdict`;

  const excluded = ['feedback', 'grain', 'particles'].filter(t => opt.expect[t]);
  console.log('');
  console.log(`  mode ${opt.mode.toUpperCase()} (${opt.modeName})  ${opt.gateType === 'per-frame'
    ? `per-frame SSIM >= ${opt.threshold}`
    : `SSIM at frame ${opt.smokeFrame} >= ${opt.smoke}`}`);
  console.log(`  excluding: ${excluded.join(' + ') || 'nothing'}`);
  console.log(`  A: ${rootA}`);
  console.log(`  B: ${rootB}`);
  console.log('');
  console.log('  ' + header);
  console.log('  ' + '-'.repeat(header.length));

  for (const r of results) {
    if (r.error) {
      console.log(`  ${pad(r.preset, w0, true)}  ${pad('--', 6)}  ${r.error}`);
      continue;
    }
    const verdict = r.pass ? 'pass' : 'FAIL';
    console.log(opt.gateType === 'per-frame'
      ? `  ${pad(r.preset, w0, true)}  ${pad(r.count, 6)}  ${f4(r.mean)}  ${f4(r.min)}  ${pad(r.worstIndex, 6)}  ${verdict}`
      : `  ${pad(r.preset, w0, true)}  ${pad(r.count, 6)}  ${f4(r.mean)}  ${f4(r.first)}  ${f4(r.smokeSsim)}  ${f4(r.last)}  ${verdict}`);
  }

  const totalFrames = graded.reduce((s, r) => s + r.count, 0);
  const overallMean = totalFrames
    ? graded.reduce((s, r) => s + r.mean * r.count, 0) / totalFrames : NaN;
  const overallMin = graded.length ? Math.min(...graded.map(r => r.min)) : NaN;
  const pass = graded.length > 0 && graded.every(r => r.pass) && results.every(r => !r.error);

  console.log('  ' + '-'.repeat(header.length));
  console.log(`  ${pad('OVERALL', w0, true)}  ${pad(totalFrames, 6)}  ${f4(overallMean)}  ${f4(overallMin)}`);
  console.log('');

  if (warnings.length) {
    console.log('  warnings:');
    for (const w of warnings) console.log(`    ! ${w}`);
    console.log('');
  }

  /* ---- mode B curve ---- */
  if (opt.gateType === 'smoke' && !opt.quiet) {
    for (const r of graded) {
      console.log(`  ${r.preset} — SSIM curve`);
      const cols = 6;
      for (let i = 0; i < r.frames.length; i += cols) {
        console.log('    ' + r.frames.slice(i, i + cols)
          .map(f => `${pad(f.index, 5)}: ${f4(f.ssim)}`).join('   '));
      }
      // A cliff is what mode B is actually looking for: expected drift is
      // gradual, so the largest single step between sampled frames is the
      // number worth printing.
      let cliff = { drop: 0, from: null, to: null };
      for (let i = 1; i < r.frames.length; i++) {
        const d = r.frames[i - 1].ssim - r.frames[i].ssim;
        if (d > cliff.drop) cliff = { drop: d, from: r.frames[i - 1].index, to: r.frames[i].index };
      }
      if (cliff.from !== null)
        console.log(`    largest single drop: ${cliff.drop.toFixed(4)} `
          + `between frame ${cliff.from} and ${cliff.to}`);
      console.log('');
    }
  }

  const report = {
    schemaVersion: 1,
    kind: 'ripple-ssim-report',
    generated: new Date().toISOString(),
    mode: opt.mode,
    modeName: opt.modeName,
    excluded: opt.expect,
    gate: opt.gateType === 'per-frame'
      ? { type: 'per-frame', threshold: opt.threshold }
      : { type: 'smoke', frame: opt.smokeFrame, threshold: opt.smoke,
          note: 'informational; feedback divergence over time is expected' },
    dirA: rootA,
    dirB: rootB,
    ssim: { window: WIN, sigma: SIGMA, c1: C1, c2: C2, luma: 'rec709' },
    amplify: opt.amplify,
    overall: {
      presets: results.length,
      graded: graded.length,
      frames: totalFrames,
      mean: Number.isNaN(overallMean) ? null : overallMean,
      min: Number.isNaN(overallMin) ? null : overallMin,
      pass,
    },
    warnings,
    presets: results,
  };
  mkdirSync(dirname(reportPath), { recursive: true });
  writeFileSync(reportPath, JSON.stringify(report, null, 2));
  console.log(`  report: ${reportPath}`);
  if (existsSync(diffDir)) console.log(`  diffs:  ${diffDir}`);

  console.log('');
  console.log(pass ? `  GATE PASS (mode ${opt.mode.toUpperCase()})`
                   : `  GATE FAIL (mode ${opt.mode.toUpperCase()})`);
  console.log('');
  return pass ? 0 : 1;
}

try {
  process.exit(main());
} catch (e) {
  console.error(`compare-ssim: ${e.stack || e.message}`);
  process.exit(2);
}
