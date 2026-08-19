#!/usr/bin/env node
/*
 * tools/extract-shaders.mjs
 *
 * Splits the fenced shader template literals out of harness/index.html into the
 * shaders/ tree. CONTRACTS.md §3 exists so that this script needs no judgment:
 * the FILE: marker carries the authoritative destination path.
 *
 *   // ===== FILE: shaders/fractal/warp_fbm.frag =====
 *   const SH_WARP_FBM = `
 *     ...
 *   `;
 *   // ===== END FILE =====
 *
 * Usage:
 *   node tools/extract-shaders.mjs [--in harness/index.html] [--out .] [--check]
 *
 *   --check  exit non-zero if any on-disk file differs from the harness copy,
 *            without writing. Use it in CI to catch a harness edit that never
 *            made it into shaders/ (or the reverse).
 */
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';

const argv = process.argv.slice(2);
const arg = (name, dflt) => {
  const i = argv.indexOf('--' + name);
  return i === -1 ? dflt : argv[i + 1];
};
const IN = resolve(arg('in', 'harness/index.html'));
const OUT = resolve(arg('out', '.'));
const CHECK = argv.includes('--check');

const FENCE = /\/\/ ===== FILE: (.+?) =====\s*\r?\n\s*const \w+ = `([\s\S]*?)`;\s*\r?\n\/\/ ===== END FILE =====/g;

const html = await readFile(IN, 'utf8');

const files = [];
let m;
while ((m = FENCE.exec(html)) !== null) {
  const path = m[1].trim();
  // The literal opens with a newline after the backtick and closes with one
  // before it. Strip exactly those so the file starts at column 0.
  let body = m[2].replace(/^\r?\n/, '').replace(/\r?\n[ \t]*$/, '') + '\n';
  // Escaped backticks inside GLSL comments are an artefact of living in a
  // template literal; they are not part of the shader.
  body = body.split('\\`').join('`');
  files.push({ path, body });
}

if (!files.length) {
  console.error('no FILE: fences found in ' + IN + ' — has the fence format changed?');
  process.exit(1);
}

const seen = new Set();
let diffs = 0;
for (const f of files) {
  if (seen.has(f.path)) {
    console.error('duplicate destination: ' + f.path);
    process.exit(1);
  }
  seen.add(f.path);
  // A real directive, not the word appearing in a comment.
  if (/^[ \t]*#version/m.test(f.body)) {
    console.error('CONTRACTS §2 violation: ' + f.path + ' contains a #version directive');
    process.exit(1);
  }
  const dest = join(OUT, f.path);
  const lines = f.body.split('\n').length - 1;
  if (CHECK) {
    const old = existsSync(dest) ? await readFile(dest, 'utf8') : null;
    if (old !== f.body) { diffs++; console.log('DIFF  ' + f.path); }
    else console.log('ok    ' + f.path);
    continue;
  }
  await mkdir(dirname(dest), { recursive: true });
  await writeFile(dest, f.body, 'utf8');
  console.log(String(lines).padStart(5) + 'L  ' + f.path);
}

if (CHECK) {
  console.log(diffs ? '\n' + diffs + ' file(s) differ' : '\nshaders/ matches the harness');
  process.exit(diffs ? 1 : 0);
}
console.log('\n' + files.length + ' shader files written under ' + OUT + '/shaders/');
