param(
  [switch]$NoBuild,
  [switch]$SkipTests,
  [int]$WaitSeconds = 9
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts\verify"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$script:failures = New-Object System.Collections.ArrayList
$script:steps = New-Object System.Collections.ArrayList
$script:appPid = 0

function Step($name) {
  Write-Output ""
  Write-Output "== $name =="
}
function Pass($label) { Write-Output ("  PASS  " + $label); [void]$script:steps.Add($label) }
function Fail($label, $detail) {
  Write-Output ("  FAIL  " + $label)
  if ($detail) { Write-Output ("        " + $detail) }
  [void]$script:failures.Add($label)
}
function Dump($tag) {
  $path = Join-Path $artifacts ("uia-" + $tag + ".txt")
  & (Join-Path $PSScriptRoot "dump-uia.ps1") -ProcessId $script:appPid -MaxDepth 12 2>&1 | Out-File -FilePath $path -Encoding utf8
  return $path
}
function AssertContains($file, $pattern, $label) {
  $hit = Select-String -Path $file -Pattern $pattern -SimpleMatch -Quiet
  if ($hit) { Pass $label } else { Fail $label ("pattern not found in " + (Split-Path -Leaf $file) + ": " + $pattern) }
}
function AssertMissing($file, $pattern, $label) {
  $hit = Select-String -Path $file -Pattern $pattern -SimpleMatch -Quiet
  if (-not $hit) { Pass $label } else { Fail $label ("unexpected pattern present: " + $pattern) }
}
function Invoke($name, $type) {
  $out = & (Join-Path $PSScriptRoot "uia-action.ps1") -Name $name -Type $type -TimeoutSeconds 12 -ProcessId $script:appPid 2>&1 | Out-String
  Write-Output ("        " + $out.Trim())
}
function Select-Item($name, $type) {
  $out = & (Join-Path $PSScriptRoot "uia-action.ps1") -Name $name -Type $type -Select -TimeoutSeconds 12 -ProcessId $script:appPid 2>&1 | Out-String
  Write-Output ("        " + $out.Trim())
}

Get-Process SurgeLink -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700

if (-not $NoBuild) {
  Step "Build (solution, x64)"
  Push-Location $root
  try {
    $out = & dotnet build SurgeLink.slnx -c Debug -p:Platform=x64 -v q -nologo 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
      $errors = ($out -split "`n" | Where-Object { $_ -match "error" } | Select-Object -First 20) -join "`n"
      Fail "solution build" $errors
      Write-Output $out
      exit 1
    }
    Pass "solution build"
  } finally { Pop-Location }
}

if (-not $SkipTests) {
  Step "Tests"
  foreach ($proj in @("tests/SurgeLink.Terminal.Tests", "tests/SurgeLink.Protocols.Ssh.Tests", "tests/SurgeLink.Core.Tests", "tests/SurgeLink.Protocols.Conformance")) {
    $out = & dotnet test (Join-Path $root ($proj + "/" + (Split-Path $proj -Leaf) + ".csproj")) -v q -nologo 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0) { Pass ("tests " + (Split-Path $proj -Leaf)) } else { Fail ("tests " + (Split-Path $proj -Leaf)) ($out | Select-Object -Last 8 | Out-String) }
  }
}

Step "Launch"
$exe = Join-Path $root "src\SurgeLink.App\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\SurgeLink.exe"
if (-not (Test-Path $exe)) { Fail "app exe exists" $exe; exit 1 }
Get-Process SurgeLink -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600
$proc = Start-Process -FilePath $exe -PassThru
$script:appPid = $proc.Id
Start-Sleep -Seconds $WaitSeconds
if ($proc.HasExited) { Fail "app launched" ("exited with " + $proc.ExitCode); exit 1 }
Pass "app launched"

$shell = Dump "01-shell"
AssertContains $shell "ControlType.Window name='SurgeLink'" "window title"
AssertMissing $shell "Render error" "no render errors in shell"
AssertContains $shell "Sessions" "sidebar pane present"

Step "Open demo session"
Invoke "Open demo session" "Button"
Start-Sleep -Seconds 5
$demo = Dump "02-demo"
AssertContains $demo "Connected" "demo session connected"
AssertContains $demo "Demo (offline)" "demo protocol shown in status bar"
AssertContains $demo "Monitor" "capability switcher shows Monitor"
AssertContains $demo "Files" "capability switcher shows Files"
AssertMissing $demo "Render error" "no render errors after connect"

Step "Files view"
Select-Item "Files" "ListItem"
Start-Sleep -Seconds 3
$files = Dump "03-files"
AssertContains $files "demo.txt" "SFTP listing shows demo.txt"
AssertContains $files "Documents" "SFTP listing shows Documents"
AssertMissing $files "Render error" "no render errors in files view"

Step "Monitor view"
Select-Item "Monitor" "ListItem"
Start-Sleep -Seconds 4
$monitor = Dump "04-monitor"
AssertContains $monitor "CPU" "monitor shows CPU"
AssertMissing $monitor "Render error" "no render errors in monitor view"

Step "Terminal view"
Select-Item "Terminal" "ListItem"
Start-Sleep -Seconds 2
$term = Dump "05-terminal"
AssertContains $term "TerminalSurface" "terminal surface present after switching back"
AssertContains $term "demo@surge" "terminal still shows the shell prompt"
AssertMissing $term "Render error" "no render errors in terminal view"

Step "New session dialog"
Invoke "New session" "Button"
Start-Sleep -Seconds 2
$dialog = Dump "06-new-session"
AssertContains $dialog "Session name" "session editor opened"
AssertContains $dialog "Host" "session editor has host field"
AssertMissing $dialog "Render error" "no render errors in dialog"
& (Join-Path $PSScriptRoot "capture-window.ps1") -ProcessId $script:appPid -OutFile (Join-Path $artifacts "ui-final.png") 2>&1 | Out-Null

Stop-Process -Id $script:appPid -Force -ErrorAction SilentlyContinue

Write-Output ""
Write-Output "================ SUMMARY ================"
Write-Output ("passed: " + $script:steps.Count)
Write-Output ("failed: " + $script:failures.Count)
foreach ($f in $script:failures) { Write-Output ("  - " + $f) }
Write-Output ("artifacts: " + $artifacts)
if ($script:failures.Count -gt 0) { exit 1 }
exit 0