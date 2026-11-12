param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Serve Windows x64 con .NET Framework 4.8.' }
$output = Join-Path $OutputDirectory 'MousePointerAreaInverter'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$executable = Join-Path $output 'MousePointerAreaInverter.exe'
$sources = @('CursorTrial.cs', 'AreaMode.cs', 'TrayApp.cs', 'AssemblyInfo.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$activeIcon = Join-Path $PSScriptRoot 'assets\pointer.ico'
$pausedIcon = Join-Path $PSScriptRoot 'assets\pointer-paused.ico'
& $compiler /nologo /optimize+ /platform:x64 /target:winexe /main:PointerApp /r:System.Windows.Forms.dll /r:System.Drawing.dll "/win32icon:$activeIcon" "/resource:$activeIcon,pointer.ico" "/resource:$pausedIcon,pointer-paused.ico" "/out:$executable" $sources
if ($LASTEXITCODE -ne 0) { throw 'Compilazione fallita. Chiudi il programma prima di ricompilare.' }
$check = Start-Process -FilePath $executable -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
if ($check.ExitCode -ne 0) { throw 'Self-test fallito.' }
$files = @($executable)
foreach ($name in @('README.md', 'CHANGELOG.md', 'palette.html')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $output -Force
    $files += Join-Path $output $name
}
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable).ProductVersion
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Versione del prodotto non valida.' }
$archive = Join-Path $OutputDirectory "MousePointerAreaInverter-v$version-windows-x64.zip"
Compress-Archive -LiteralPath $files -DestinationPath $archive -Force
$checksum = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archive.sha256", $checksum + '  ' + (Split-Path $archive -Leaf) + "`n", [Text.Encoding]::ASCII)
Write-Output "Self-test superato. Pacchetto: $archive"
