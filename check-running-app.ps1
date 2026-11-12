param([switch]$Observe, [switch]$Calibration)
$ErrorActionPreference = 'Stop'
if (-not (Get-Process -Name MousePointerAreaInverter -ErrorAction SilentlyContinue)) {
    throw 'Avvia prima il pacchetto MousePointerAreaInverter.exe e verifica che sia attivo, non in pausa.'
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$executable = Join-Path $PSScriptRoot 'CheckRunningApp.exe'
$sources = @('CursorTrial.cs', 'AreaMode.cs', 'CheckRunningApp.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /optimize+ /platform:x64 /main:CheckRunningApp /r:System.Windows.Forms.dll /r:System.Drawing.dll "/out:$executable" $sources
if ($LASTEXITCODE -ne 0) { throw 'Running-app check compilation failed.' }
if ($Observe) {
    Write-Output 'Read-only observation for 6 seconds; you may move the pointer.'
    & $executable --observe
} elseif ($Calibration) {
    Write-Output 'Calibration check: leave the mouse still for about 6 seconds.'
    & $executable --calibration
} else {
    Write-Output 'Checking the REAL running package: leave the mouse still for about 3 seconds.'
    & $executable
}
if ($LASTEXITCODE -ne 0) { throw 'Running application check failed.' }
