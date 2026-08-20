<#
.SYNOPSIS
  Capture a real-audio Feature Frame timeline for preset work and DSP regression.

.DESCRIPTION
  Wraps `Ripple.exe --record-features`. It exists for one non-obvious reason: Ripple is
  built as a WinExe so the shipped app never flashes a console, and PowerShell does not
  WAIT for a WinExe. Invoking the exe directly returns immediately, `$LASTEXITCODE` comes
  back empty, and the capture file appears some seconds later — or not, if the shell
  session ends first. Start-Process -Wait is the fix, and putting it in a script means
  nobody has to rediscover that mid-session.

  Recording runs on the analysis thread, one row per DSP hop (93.75 rows/s at 48 kHz), so
  the capture rate does not depend on how fast this machine renders.

  Captures land in tests\fixtures\real\<name>.json by default — committed as fixtures,
  because they are small JSON and their value is as a regression baseline rather than as a
  one-time measurement.

.PARAMETER Name
  Short file name, no extension. Becomes tests\fixtures\real\<Name>.json.

.PARAMETER Label
  What this capture is OF — the track, the era, why it is interesting. Stored in the
  timeline itself, so the fixture stays self-describing.

.PARAMETER Seconds
  Capture length. 60 is the useful default: long enough for the 8 s novelty EMA and the
  tempo median to settle several times over.

.EXAMPLE
  .\scripts\capture.ps1 tight-backbeat "Sugar Magnolia, 1972 studio - rock solid tempo"

.EXAMPLE
  .\scripts\capture.ps1 free-time "Drums>Space, Cornell 5/8/77" -Seconds 90
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true, Position = 0)]
  [string] $Name,

  [Parameter(Mandatory = $true, Position = 1)]
  [string] $Label,

  [double] $Seconds = 60,

  [string] $OutDir = "",

  [ValidateSet('Release', 'Debug')]
  [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $repo 'tests\fixtures\real' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$out = Join-Path $OutDir "$Name.json"
$tfm = 'net10.0-windows10.0.19041.0'
$exe = Join-Path $repo "src\Ripple\bin\$Configuration\$tfm\win-x64\Ripple.exe"

if (-not (Test-Path -LiteralPath $exe)) {
  throw "No $Configuration build at $exe`nRun:  .\build.ps1 -Configuration $Configuration"
}

if ($Configuration -ne 'Release') {
  Write-Host "NOTE: a Debug build reports DSP cost about 2.5x high. Measure in Release." `
    -ForegroundColor Yellow
}

Write-Host ""
Write-Host "  capture : $Name" -ForegroundColor Cyan
Write-Host "  label   : $Label"
Write-Host "  length  : $Seconds s"
Write-Host "  output  : $out"
Write-Host ""
Write-Host "  Start playback now, then leave it alone. A status line every 2 s reports"
Write-Host "  level, the eight bands, tempo, key, percussivity and stereo width."
Write-Host ""

# Quote each value: Start-Process joins ArgumentList with spaces WITHOUT quoting, so a
# label like "Sugar Magnolia, 1972" arrives as three separate arguments and the app
# rejects the second one. Same hazard for a path containing a space.
$q = { param($v) '"' + ($v -replace '"', '\"') + '"' }
$procArgs = @(
  '--record-features', (& $q $out),
  '--record-seconds', $Seconds.ToString([cultureinfo]::InvariantCulture),
  '--record-label', (& $q $Label)
)

# -NoNewWindow so the app's console output lands here; -Wait because a WinExe otherwise
# returns immediately and the file would not exist yet when this script finishes.
$proc = Start-Process -FilePath $exe -ArgumentList $procArgs -NoNewWindow -Wait -PassThru
$code = $proc.ExitCode

Write-Host ""
if ($code -ne 0) {
  Write-Host "  capture exited with code $code" -ForegroundColor Red
  exit $code
}

if (-not (Test-Path -LiteralPath $out)) {
  Write-Host "  no file written - was anything playing?" -ForegroundColor Red
  exit 1
}

$size = [math]::Round((Get-Item -LiteralPath $out).Length / 1KB)
Write-Host "  wrote $out ($size KB)" -ForegroundColor Green
Write-Host ""
Write-Host "  Inspect it with:"
Write-Host "    node tools/timeline-stats.mjs `"$out`""
Write-Host ""
