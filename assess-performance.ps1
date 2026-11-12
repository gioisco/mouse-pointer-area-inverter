param(
    [Parameter(Mandatory = $true)][string]$Report,
    [ValidateRange(0.001, 100)][double]$BudgetPercent = 0.5
)
$ErrorActionPreference = 'Stop'
$rows = @(Import-Csv -LiteralPath $Report)
function Number([string]$Value) {
    $parsed = [double]::Parse($Value, [Globalization.CultureInfo]::InvariantCulture)
    if ([double]::IsNaN($parsed) -or [double]::IsInfinity($parsed)) { throw "Unavailable CPU/control value: $Value" }
    return $parsed
}
$coreCounts = @($rows.logical_processors | Sort-Object -Unique)
if ($coreCounts.Count -ne 1 -or (Number $coreCounts[0]) -lt 1) { throw 'Inconsistent logical processor count.' }
$cores = Number $coreCounts[0]
if (@($rows | Where-Object { $_.dwm_cpu_status -ne 'available' -or $_.gpu_status -ne 'available' -or $_.gpu_errors -ne '0' }).Count) {
    throw 'Incomplete or failed resource counters; do not treat this report as a completed comparison.'
}
$rounds = @($rows.round | Sort-Object -Unique)
if ($rounds.Count -lt 3) { throw 'At least three complete rounds are required for a budget assessment.' }
$results = @(foreach ($scenario in @('stationary', 'moving')) {
    $increments = @()
    $appCpu = @()
    foreach ($round in $rounds) {
        foreach ($mode in @('normal', 'native-inverted', 'adaptive')) {
            $matches = @($rows | Where-Object { $_.scenario -eq $scenario -and $_.round -eq $round -and $_.mode -eq $mode })
            if ($matches.Count -ne 1) { throw "Missing/duplicate condition: $scenario, round $round, $mode" }
        }
        $baseline = $rows | Where-Object { $_.scenario -eq $scenario -and $_.round -eq $round -and $_.mode -eq 'normal' }
        $adaptive = $rows | Where-Object { $_.scenario -eq $scenario -and $_.round -eq $round -and $_.mode -eq 'adaptive' }
        $app = Number $adaptive.application_cpu_ms_per_s
        $delta = $app + (Number $adaptive.dwm_cpu_ms_per_s) - (Number $baseline.application_cpu_ms_per_s) - (Number $baseline.dwm_cpu_ms_per_s)
        $increments += $delta / (10 * $cores)
        $appCpu += $app
    }
    $mean = ($increments | Measure-Object -Average).Average
    [pscustomobject]@{
        Scenario = $scenario
        Rounds = $rounds.Count
        AppCpuMsPerSecond = [math]::Round(($appCpu | Measure-Object -Average).Average, 3)
        ObservedAddedCpuPercent = [math]::Round($mean, 4)
        MinimumRoundPercent = [math]::Round(($increments | Measure-Object -Minimum).Minimum, 4)
        MaximumRoundPercent = [math]::Round(($increments | Measure-Object -Maximum).Maximum, 4)
        BudgetPercent = $BudgetPercent
        MeanWithinBudget = $mean -le $BudgetPercent
    }
})
if ($rows.Count -ne $rounds.Count * 6) { throw 'Unexpected extra conditions in the runtime report.' }
# This assesses the agreed mean of observed components, not causality or visual/GPU latency.
$results
