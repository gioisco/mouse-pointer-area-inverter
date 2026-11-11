$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Join-Path $PSScriptRoot 'Probe.cs'
$executable = Join-Path $PSScriptRoot 'Probe.exe'
$report = Join-Path $PSScriptRoot ('probe-{0}.csv' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
& $compiler /nologo /optimize+ /platform:x64 "/out:$executable" $source
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
& $executable $report
if ($LASTEXITCODE -ne 0) { throw 'Benchmark failed. Run from your interactive Windows desktop.' }
Import-Csv -LiteralPath $report | Format-Table
