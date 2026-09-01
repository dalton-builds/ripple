<#
.SYNOPSIS
  Render an A/B of the rhythm layer: same timeline, rhythm on versus rhythm off.

.DESCRIPTION
  The control is not a separate build. Everything derived from the BEAT GRID is scaled
  by bpmConfidence — the beatPhase and barPhase mapping routes via RhythmicShape, and
  warp_fbm's own scale and rotation pulses via F_BPM_CONF — so a timeline with its
  bpmConf column zeroed turns all of it off and changes nothing else. Same features,
  same presets, same shaders, same build.

  It isolates the grid-derived response specifically, NOT the whole rhythm layer. The
  onset -> warpAmount and flux -> fractalMorph routes are deliberately not gated: they
  read features directly rather than a grid, so they keep working when the tracker has
  no confidence, which is the behaviour you want in a free-time passage. Measured, they
  are bit-identical between the two sides; only zoom moves (depth 76% -> 5%, correlation
  with the beat 0.999 -> 0.025).

  Frames are sampled densely across two beats rather than spread over the run, because
  the thing being looked at is a pulse. At 124 BPM a beat is 484 ms, so 60 frames at
  60 fps covers just over two beats; stride 2 gives 30 PNGs per side.

.EXAMPLE
  .\scripts\capture-rhythm-ab.ps1
  .\scripts\capture-rhythm-ab.ps1 -Preset era-brent -Frames 120 -Stride 4
#>
[CmdletBinding()]
param(
  [string] $Preset = 'era-europe72',
  [int]    $Frames = 60,
  [int]    $Stride = 2,
  [string] $Out = 'captures/ab'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo 'src\Ripple\bin\Release\net10.0-windows10.0.19041.0\win-x64\Ripple.exe'
if (-not (Test-Path $exe)) { throw "build Release first: .\build.ps1 -Configuration Release" }

$full = Join-Path $repo 'tests\fixtures\timeline-budget-60s.json'
$outDir = Join-Path $repo $Out
$noconf = Join-Path $outDir 'timeline-noconf.json'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# Build the control: identical timeline with bpmConf zeroed, and trimmed to the window
# so the dump does not render 3600 frames to keep 30.
$build = @'
const fs = require('node:fs');
const [src, dstOn, dstOff, frames] = process.argv.slice(2);
const tl = JSON.parse(fs.readFileSync(src, 'utf8'));
const key = tl.rows ? 'rows' : (tl.data ? 'data' : 'frames');
const ci = tl.fieldOrder.indexOf('bpmConf');
const n = Number(frames);

// Start a couple of seconds in, past the point where a preset's feedback buffer has
// filled: a dump that starts at frame 0 shows an empty screen warming up.
const from = 600;
const win = tl[key].slice(from, from + n).map(r => r.slice());

const on = { ...tl, [key]: win, seconds: n / tl.rate };
fs.writeFileSync(dstOn, JSON.stringify(on));

const off = { ...tl, [key]: win.map(r => { const c = r.slice(); c[ci] = 0; return c; }),
              seconds: n / tl.rate,
              note: (tl.note || '') + ' | A/B CONTROL: bpmConf zeroed, which disables every '
                  + 'rhythmic mapping route by design. Identical in every other field.' };
fs.writeFileSync(dstOff, JSON.stringify(off));
console.log(`  window frames ${from}..${from + n}, bpmConf at index ${ci}`);
'@
$buildFile = Join-Path $env:TEMP 'ripple-ab-build.cjs'
Set-Content -Path $buildFile -Value $build -Encoding utf8

$on = Join-Path $outDir 'timeline-on.json'
node $buildFile $full $on $noconf $Frames

foreach ($side in @(@{ Name = 'rhythm-on'; Timeline = $on }, @{ Name = 'rhythm-off'; Timeline = $noconf })) {
  $dir = Join-Path $outDir $side.Name
  if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
  Write-Host "[ab] $($side.Name)"
  & $exe --dump --timeline $side.Timeline --out $dir --preset $Preset --stride $Stride --trace
  if ($LASTEXITCODE -ne 0) { throw "dump failed for $($side.Name)" }
}

Write-Host ''
Write-Host "[ab] wrote $outDir\rhythm-on and $outDir\rhythm-off ($Preset)"
Write-Host "[ab] flip through the two folders frame by frame; the difference is the rhythm layer."
Write-Host "[ab] numbers:  node tools/rhythm-depth.mjs $Out/rhythm-on/$Preset/trace.json $Out/timeline-on.json"
