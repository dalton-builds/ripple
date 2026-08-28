<#
.SYNOPSIS
  Run Ripple with the SDK version global.json pins.

.DESCRIPTION
  Same SDK resolution as build.ps1 (see scripts\dotnet-resolve.ps1) — PATH is
  not trusted to point at an SDK install. Everything after the wrapper's own
  switches is forwarded to the app verbatim.

.EXAMPLE
  .\run.ps1
  .\run.ps1 --preset era-europe72 --size 960x540
  .\run.ps1 --dump --timeline captures\timeline-20s.json --out captures\native --all
#>
[CmdletBinding()]
param(
  [ValidateSet('Debug', 'Release')]
  [string] $Configuration = 'Debug',
  [switch] $NoBuild,
  # Position 0 on AppArgs is what makes -Configuration name-only. Without it
  # PowerShell binds the first app argument (`--dump`) to Configuration and the
  # ValidateSet rejects it.
  [Parameter(ValueFromRemainingArguments = $true, Position = 0)]
  [string[]] $AppArgs
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. "$PSScriptRoot\scripts\dotnet-resolve.ps1"
$dotnet = Resolve-RippleDotnet

$cliArgs = @('run', '--project', "$PSScriptRoot\src\Ripple\Ripple.csproj", '-c', $Configuration, '--nologo')

# These three lines appended to $args, PowerShell's AUTOMATIC argument variable, rather than
# to $cliArgs. Under Set-StrictMode that silently built a throwaway array: -NoBuild did
# nothing, and the `--` separator never reached the muxer, so app arguments were handed to
# the SDK. `.\run.ps1 --budget-test` failed with an unrecognised-command error from dotnet.
if ($NoBuild) { $cliArgs += '--no-build' }
# Everything past `--` is the app's, not the SDK's.
$cliArgs += '--'
if ($AppArgs) { $cliArgs += $AppArgs }

& $dotnet @cliArgs
exit $LASTEXITCODE
