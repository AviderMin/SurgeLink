param(
  [Parameter(Mandatory=$true)][string]$Agent,
  [switch]$NoBuild,
  [int]$WaitSeconds = 8,
  [string]$Tag = "",
  [switch]$KeepRunning
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Tag) { $Tag = $Agent }

# Stop only instances started from THIS agent's artifact folder, so parallel agents do not kill each other.
function Stop-OwnInstances($exePath) {
  Get-Process SurgeLink -ErrorAction SilentlyContinue | ForEach-Object {
    try {
      if ($_.Path -eq $exePath) { Stop-Process -Id $_.Id -Force }
    } catch { }
  }
}

$binRoot = Join-Path $root "artifacts\agent-build\$Agent\bin"
$exe = $null
if (Test-Path $binRoot) {
  $exe = Get-ChildItem $binRoot -Recurse -Filter "SurgeLink.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
}
if ($exe) { Stop-OwnInstances $exe.FullName }
Start-Sleep -Milliseconds 500

if (-not $NoBuild) {
  $out = & (Join-Path $PSScriptRoot "agent-build.ps1") -Project "src/SurgeLink.App/SurgeLink.App.csproj" -Agent $Agent
  if ($LASTEXITCODE -ne 0) {
    Write-Output "BUILD FAILED for agent $Agent"
    Write-Output ($out | Select-Object -Last 40 | Out-String)
    exit 1
  }
  $exe = Get-ChildItem $binRoot -Recurse -Filter "SurgeLink.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($exe) { Stop-OwnInstances $exe.FullName }
  Start-Sleep -Milliseconds 600
}

if (-not $exe) { Write-Output "MISSING EXE under $binRoot"; exit 2 }

$artifacts = Join-Path $root "artifacts"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

$p = Start-Process -FilePath $exe.FullName -PassThru
Start-Sleep -Seconds $WaitSeconds
if ($p.HasExited) { Write-Output "APP EXITED code=$($p.ExitCode)"; exit 3 }

$uia = Join-Path $artifacts "uia-$Tag.txt"
& (Join-Path $PSScriptRoot "dump-uia.ps1") -ProcessId $p.Id -MaxDepth 10 2>&1 | Out-File -FilePath $uia -Encoding utf8
$png = Join-Path $artifacts "ui-$Tag.png"
& (Join-Path $PSScriptRoot "capture-window.ps1") -ProcessId $p.Id -OutFile $png 2>&1 | Out-Null
Write-Output "PID=$($p.Id)"
Write-Output "EXE=$($exe.FullName)"
Write-Output "UIA=$uia"
Write-Output "PNG=$png"
if (-not $KeepRunning) { Stop-Process -Id $p.Id -Force; Write-Output "STOPPED" }
