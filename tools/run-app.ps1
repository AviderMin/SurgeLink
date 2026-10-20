param(
  [switch]$NoBuild,
  [int]$WaitSeconds = 8,
  [switch]$KeepRunning,
  [string]$Tag = "latest",
  [string[]]$LaunchArgs = @()
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\SurgeLink.App\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\SurgeLink.exe"

function Stop-OwnInstances($exePath) {
  Get-Process SurgeLink -ErrorAction SilentlyContinue | ForEach-Object {
    try { if ($_.Path -eq $exePath) { Stop-Process -Id $_.Id -Force } } catch { }
  }
}
Stop-OwnInstances $exe
Start-Sleep -Milliseconds 500

if (-not $NoBuild) {
  Push-Location $root
  try {
    $out = & dotnet build "src/SurgeLink.App/SurgeLink.App.csproj" -c Debug -p:Platform=x64 -v q -nologo 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { Write-Output "BUILD FAILED"; Write-Output $out; exit 1 }
  } finally { Pop-Location }
  Stop-OwnInstances $exe
  Start-Sleep -Milliseconds 500
}

$artifacts = Join-Path $root "artifacts"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
if (-not (Test-Path $exe)) { Write-Output "MISSING EXE: $exe"; exit 2 }

if ($LaunchArgs.Count -gt 0) { $p = Start-Process -FilePath $exe -ArgumentList $LaunchArgs -PassThru } else { $p = Start-Process -FilePath $exe -PassThru }
Start-Sleep -Seconds $WaitSeconds
if ($p.HasExited) { Write-Output "APP EXITED code=$($p.ExitCode)"; exit 3 }

$uia = Join-Path $artifacts "uia-$Tag.txt"
& (Join-Path $PSScriptRoot "dump-uia.ps1") -ProcessId $p.Id -MaxDepth 10 2>&1 | Out-File -FilePath $uia -Encoding utf8
$png = Join-Path $artifacts "ui-$Tag.png"
& (Join-Path $PSScriptRoot "capture-window.ps1") -ProcessId $p.Id -OutFile $png 2>&1 | Out-Null
Write-Output "PID=$($p.Id)"
Write-Output "UIA=$uia"
Write-Output "PNG=$png"
if (-not $KeepRunning) { Stop-Process -Id $p.Id -Force; Write-Output "STOPPED" }
