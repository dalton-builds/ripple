// Per-band gamma fit. Reads a real capture and the synthetic driver's medians, and
// solves gamma[b] = ln(target) / ln(measured_pre_gamma) for each band.
import { readFileSync } from 'node:fs';

const COMMITTED = 4.66;
const BANDS = ['sub','bass','lowMid','mid','hiMid','presence','brilliance','air'];
const SCALARS = ['rms','peak','loudSlow'];

// Synthetic-driver medians, the authoring reference — from NormalizationTests output.
const SYN = {
  sub:0.307, bass:0.457, lowMid:0.393, mid:0.373, hiMid:0.344,
  presence:0.227, brilliance:0.267, air:0.212,
  rms:0.437, peak:0.550, loudSlow:0.420,
};

const tl = JSON.parse(readFileSync(process.argv[2],'utf8'));
const order = tl.fieldOrder;
const rows = tl.rows ?? tl.data ?? tl.frames;
const col = (name) => {
  const i = order.indexOf(name);
  return rows.map(r => r[i]).filter(v => Number.isFinite(v));
};
const median = (v) => { const s=[...v].sort((a,b)=>a-b); return s[Math.floor(s.length/2)]; };

console.log(`  ${'field'.padEnd(12)} ${'real p50'.padStart(9)} ${'pre-gamma'.padStart(10)} ${'synthetic'.padStart(10)} ${'gamma'.padStart(8)}`);
console.log('  ' + '-'.repeat(54));
const out = {};
for (const f of [...BANDS, ...SCALARS]) {
  const post = median(col(f));
  // Invert the committed curve to recover what the normalizer produced before calibration.
  const pre = Math.pow(post, 1/COMMITTED);
  const target = SYN[f];
  // pre^g = target  ->  g = ln(target)/ln(pre)
  const g = Math.log(target) / Math.log(Math.min(Math.max(pre,1e-6),0.999999));
  out[f] = g;
  console.log(`  ${f.padEnd(12)} ${post.toFixed(3).padStart(9)} ${pre.toFixed(3).padStart(10)} ${target.toFixed(3).padStart(10)} ${g.toFixed(2).padStart(8)}`);
}
console.log('');
console.log('  bands, in FF order:');
console.log('  [' + BANDS.map(b => out[b].toFixed(2)).join(', ') + ']');
console.log('  scalars: rms ' + out.rms.toFixed(2) + '  peak ' + out.peak.toFixed(2) + '  loudSlow ' + out.loudSlow.toFixed(2));
