$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$executable = Join-Path $PSScriptRoot 'CursorTrial.exe'
$check = Join-Path $PSScriptRoot 'CheckShapes.exe'
& $compiler /nologo /optimize+ /platform:x64 "/out:$executable" (Join-Path $PSScriptRoot 'CursorTrial.cs') (Join-Path $PSScriptRoot 'AreaMode.cs')
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
& $executable --self-test
if ($LASTEXITCODE -ne 0) { throw 'Self-test failed.' }
& $compiler /nologo /optimize+ /platform:x64 /r:System.Windows.Forms.dll /r:System.Drawing.dll "/out:$check" (Join-Path $PSScriptRoot 'CheckShapes.cs')
if ($LASTEXITCODE -ne 0) { throw 'Shape check compilation failed.' }
& $check
if ($LASTEXITCODE -ne 0) { throw 'Standard cursor verification failed.' }
