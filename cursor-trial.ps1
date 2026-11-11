$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Join-Path $PSScriptRoot 'CursorTrial.cs'
$executable = Join-Path $PSScriptRoot 'CursorTrial.exe'
& $compiler /nologo /optimize+ /platform:x64 "/out:$executable" $source (Join-Path $PSScriptRoot 'AreaMode.cs')
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
& $executable --self-test
if ($LASTEXITCODE -ne 0) { throw 'Self-test failed.' }
& $executable --trial
if ($LASTEXITCODE -ne 0) { throw 'Cursor trial failed; see console output.' }
