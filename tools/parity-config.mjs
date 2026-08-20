/*
 * tools/parity-config.mjs
 *
 * Shared reader for tests/parity-config.json.
 *
 * The exclusions the strict gate depends on used to live in whichever CLI flags the
 * person running it remembered. That is fine once and wrong forever after: when Phase 4
 * touches the composite pass and the gate is re-run, it has to re-run with the SAME
 * exclusions that established the baseline, or the comparison is against a different
 * experiment. One reader, used by both tools and mirrored by src/Ripple/ParityProfile.cs,
 * is what keeps them from drifting.
 */
import { readFileSync } from 'node:fs';

export function loadParityConfig(path) {
  const cfg = JSON.parse(readFileSync(path, 'utf8'));
  if (cfg.kind !== 'ripple-parity-config')
    throw new Error(`${path}: not a ripple parity config`);
  return cfg;
}

/** The terms a host knows how to exclude. An unknown one is a loud failure. */
export const TERMS = ['feedback', 'grain', 'particles'];

export function parityMode(cfg, mode, path) {
  const m = cfg.modes && cfg.modes[mode];
  if (!m)
    throw new Error(`${path}: no mode '${mode}' `
      + `(have: ${Object.keys(cfg.modes || {}).join(', ') || 'none'})`);

  // Every term must be stated, so a gate run is never ambiguous about what it measured.
  for (const term of TERMS) {
    if (typeof m.exclude?.[term] !== 'boolean')
      throw new Error(`${path}: mode '${mode}' does not say whether to exclude '${term}'`);
  }
  for (const term of Object.keys(m.exclude)) {
    if (!TERMS.includes(term))
      throw new Error(`${path}: mode '${mode}' excludes unknown term '${term}'. `
        + 'Adding an exclusion needs host support and a recorded measurement.');
  }
  return m;
}
