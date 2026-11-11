param(
    [ValidateRange(4, 120)][int]$Seconds = 60,
    [ValidateRange(1, 8)][int]$Rounds = 3
)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$executable = Join-Path $PSScriptRoot 'CompareNative.exe'
& $compiler /nologo /optimize+ /platform:x64 /main:CompareNative /r:System.Windows.Forms.dll /r:System.Drawing.dll "/out:$executable" (Join-Path $PSScriptRoot 'CursorTrial.cs') (Join-Path $PSScriptRoot 'AreaMode.cs') (Join-Path $PSScriptRoot 'CompareNative.cs')
if ($LASTEXITCODE -ne 0) { throw 'Comparison compilation failed.' }
& $executable $Seconds $Rounds | Tee-Object -Variable comparisonOutput
if ($LASTEXITCODE -ne 0) { throw 'Comparison failed or was cancelled; partial measurements may have been saved.' }
$reportLine = $comparisonOutput | Where-Object { $_ -like 'Report: *' } | Select-Object -Last 1
if (-not $reportLine) { throw 'Comparison report path missing.' }
$rows = Import-Csv -LiteralPath $reportLine.Substring(8)
$means = @($rows | Group-Object mode | ForEach-Object {
    $values = @($_.Group | ForEach-Object { [double]::Parse($_.process_cpu_ms_per_s, [Globalization.CultureInfo]::InvariantCulture) })
    [pscustomobject]@{ Mode = $_.Name; MeanCpuMsPerSecond = ($values | Measure-Object -Average).Average }
})
$baseline = ($means | Where-Object Mode -eq 'normal').MeanCpuMsPerSecond
Write-Output 'Process CPU includes the replay window. Deltas are relative to the same window with the normal native cursor.'
$means | Select-Object Mode, @{ Name = 'MeanCpuMsPerSecond'; Expression = { [math]::Round($_.MeanCpuMsPerSecond, 3) } }, @{ Name = 'DeltaVsNormalMsPerSecond'; Expression = { [math]::Round($_.MeanCpuMsPerSecond - $baseline, 3) } } | Format-Table
