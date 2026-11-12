param([ValidateRange(8, 60)][int]$Seconds = 20, [ValidateRange(1, 4)][int]$Rounds = 3)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$application = Join-Path $PSScriptRoot 'CursorTrial.exe'
$executable = Join-Path $PSScriptRoot 'PerformanceGate.exe'
$core = @((Join-Path $PSScriptRoot 'CursorTrial.cs'), (Join-Path $PSScriptRoot 'AreaMode.cs'))
& $compiler /nologo /optimize+ /platform:x64 "/out:$application" $core
if ($LASTEXITCODE -ne 0) { throw 'Adaptive application compilation failed.' }
& $application --self-test
if ($LASTEXITCODE -ne 0) { throw 'Adaptive self-test failed.' }
& $compiler /nologo /optimize+ /platform:x64 /main:PerformanceGate /r:System.Windows.Forms.dll /r:System.Drawing.dll "/out:$executable" $core (Join-Path $PSScriptRoot 'CompareNative.cs') (Join-Path $PSScriptRoot 'PerformanceGate.cs')
if ($LASTEXITCODE -ne 0) { throw 'Runtime comparison compilation failed.' }
Write-Output 'The adaptive conditions start the REAL application and invert the cursor. Native conditions use Windows alone. Both stationary and moving scenarios are measured.'
& $executable $Seconds $Rounds
if ($LASTEXITCODE -ne 0) { throw 'Runtime comparison failed or was cancelled; partial results may have been retained.' }
