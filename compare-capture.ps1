param(
    [ValidateRange(4, 120)][int]$Seconds = 10,
    [ValidateRange(1, 8)][int]$Rounds = 3,
    [ValidateCount(1, 5)][ValidateSet('baseline','gdi','gdi-monitor','gdi-transient','wgc')][string[]]$Modes = @('baseline','gdi','wgc')
)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$runtimeFacade = [Reflection.Assembly]::Load('System.Runtime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a').Location
$runtimeInterop = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll'
$graphicsMetadata = Join-Path $env:WINDIR 'System32\WinMetadata\Windows.Graphics.winmd'
$foundationMetadata = Join-Path $env:WINDIR 'System32\WinMetadata\Windows.Foundation.winmd'
$executable = Join-Path $PSScriptRoot 'WgcProbe.exe'
& $compiler /nologo /optimize+ /platform:x64 /main:WgcProbe /r:System.Windows.Forms.dll /r:System.Drawing.dll "/r:$runtimeFacade" "/r:$runtimeInterop" "/r:$graphicsMetadata" "/r:$foundationMetadata" "/out:$executable" (Join-Path $PSScriptRoot 'experiments\capture\CaptureProbe.cs') (Join-Path $PSScriptRoot 'experiments\capture\WgcProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Capture probe compilation failed.' }
$report = Join-Path $PSScriptRoot ('capture-cost-{0}.csv' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
$modes = @($Modes | ForEach-Object { $_.ToLowerInvariant() })
Write-Output 'Capture-only benchmark: the cursor moves but its color does not change. The adaptive application is not running in this test.'
for ($round = 0; $round -lt $Rounds; $round++) {
    for ($condition = 0; $condition -lt $modes.Count; $condition++) {
        $mode = $modes[($round + $condition) % $modes.Count]
        Write-Output "Round $($round + 1)/${Rounds}: $mode"
        & $executable $mode $Seconds | Tee-Object -Variable probeOutput
        if ($LASTEXITCODE -ne 0) { throw "Capture comparison failed in $mode; completed measurements are in $report" }
        $metric = $probeOutput | Where-Object { $_ -like 'METRIC,*' } | Select-Object -Last 1
        if (-not $metric) { throw 'Probe metrics missing.' }
        $row = $metric.Substring(7) | ConvertFrom-Csv -Header mode,seconds,process_cpu_ms_per_s,system_busy_percent,samples,capture_p95_ms,working_set_bytes,dwm_cpu_ms_per_s
        $row | Select-Object @{ Name = 'round'; Expression = { $round + 1 } }, * | Export-Csv -LiteralPath $report -Append -NoTypeInformation
    }
}
Write-Output "Report: $report"
Import-Csv -LiteralPath $report | Format-Table
