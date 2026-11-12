# Builds everything and runs the unit tests (optional arg: class-name filter). Exit code = failures.
param([string]$Filter = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $root 'build.ps1') -Tests
$exe = Join-Path $root 'tests\bin\UnitTests.exe'
if ($Filter) { & $exe $Filter | Out-Host } else { & $exe | Out-Host }
exit $LASTEXITCODE
