param([ValidateRange(1, 3600)][int]$Seconds = 60)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$executable = Join-Path $PSScriptRoot 'CursorTrial.exe'
& $compiler /nologo /optimize+ /platform:x64 "/out:$executable" (Join-Path $PSScriptRoot 'CursorTrial.cs') (Join-Path $PSScriptRoot 'AreaMode.cs')
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed. Stop any running trial before rebuilding.' }
& $executable --self-test
if ($LASTEXITCODE -ne 0) { throw 'Self-test failed.' }
& $executable --adaptive-all $Seconds
if ($LASTEXITCODE -ne 0) { throw 'Adaptive run failed; see console output.' }
