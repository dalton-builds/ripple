<#
  Shared SDK resolution for build.ps1 and run.ps1.

  Dot-source this, then call Resolve-RippleDotnet. It returns the path to a
  dotnet muxer that actually has an SDK, or throws with the list of what it
  tried. Keeping it in one file means the two wrappers cannot drift.
#>

function Test-RippleSdk {
  param([string] $Muxer)
  if (-not $Muxer) { return $false }
  if (-not (Test-Path -LiteralPath $Muxer)) { return $false }
  # --list-sdks writes nothing on a runtime-only install. Swallow its stderr:
  # a missing SDK is a "no" here, not an error to propagate.
  $sdks = & $Muxer --list-sdks 2>$null
  return [bool]$sdks
}

function Resolve-RippleDotnet {
  $candidates = @()
  if ($env:DOTNET_ROOT) { $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }
  $candidates += (Join-Path $HOME '.dotnet\dotnet.exe')
  $candidates += 'C:\Program Files\dotnet\dotnet.exe'
  $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
  if ($onPath) { $candidates += $onPath.Source }

  foreach ($c in $candidates) {
    if (Test-RippleSdk $c) { return $c }
  }

  throw @"
No .NET SDK found. Ripple needs the SDK pinned in global.json (10.0.400 or a
later 10.0.4xx feature band); the runtime alone is not enough.

Tried:
$($candidates | ForEach-Object { "  $_" } | Out-String)
Install it with:
  winget install Microsoft.DotNet.SDK.10
or set DOTNET_ROOT to an install that has one.
"@
}
