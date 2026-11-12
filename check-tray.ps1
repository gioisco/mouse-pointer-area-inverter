$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$executable = Join-Path $PSScriptRoot 'CheckTray.exe'
$sources = @('CursorTrial.cs', 'AreaMode.cs', 'TrayApp.cs', 'CheckTray.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$activeIcon = Join-Path $PSScriptRoot 'assets\pointer.ico'
$pausedIcon = Join-Path $PSScriptRoot 'assets\pointer-paused.ico'
& $compiler /nologo /optimize+ /platform:x64 /main:CheckTray /r:System.Windows.Forms.dll /r:System.Drawing.dll "/resource:$activeIcon,pointer.ico" "/resource:$pausedIcon,pointer-paused.ico" "/out:$executable" $sources
if ($LASTEXITCODE -ne 0) { throw 'Tray check compilation failed.' }
Write-Output 'Interactive tray check: do not move the mouse or click for about 5 seconds.'
& $executable
if ($LASTEXITCODE -ne 0) { throw 'Tray lifecycle check failed.' }
