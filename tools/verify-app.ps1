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
$appData = Join-Path $env:LOCALAPPDATA 'SurgeLink'

function Step($name) { Write-Output ""; Write-Output "== $name ==" }
function Pass($label) { Write-Output ("  PASS  " + $label); [void]$script:steps.Add($label) }
function Fail($label, $detail) {
  Write-Output ("  FAIL  " + $label)
  if ($detail) { Write-Output ("        " + $detail) }
  [void]$script:failures.Add($label)
}
function Set-Language([int]$language) {
  New-Item -ItemType Directory -Force -Path $appData | Out-Null
  $file = Join-Path $appData 'settings.json'
  $json = '{"language":' + $language + '}'
  [System.IO.File]::WriteAllText($file, $json)
}
function Dump($tag) {
  $path = Join-Path $artifacts ("uia-" + $tag + ".txt")
  & (Join-Path $PSScriptRoot "dump-uia.ps1") -ProcessId $script:appPid -MaxDepth 12 2>&1 | Out-File -FilePath $path -Encoding utf8
  return $path
}
function AssertContains($file, $pattern, $label) {
  if (Select-String -Path $file -Pattern $pattern -SimpleMatch -Quiet) { Pass $label } else { Fail $label ("pattern not found: " + $pattern) }
}
function AssertMissing($file, $pattern, $label) {
  if (-not (Select-String -Path $file -Pattern $pattern -SimpleMatch -Quiet)) { Pass $label } else { Fail $label ("unexpected pattern: " + $pattern) }
}
function AssertCjk($file, $minimum, $label) {
  $count = ([regex]::Matches((Get-Content $file -Raw), '[\u4e00-\u9fff]')).Count
  if ($count -ge $minimum) { Pass ($label + " (" + $count + " 汉字)") } else { Fail $label ("only " + $count + " CJK characters") }
}
function Invoke-Action($name, $type, [switch]$Select) {
  $args = @{ Name = $name; Type = $type; TimeoutSeconds = 12; ProcessId = $script:appPid }
  if ($Select) { $args['Select'] = $true }
  $out = & (Join-Path $PSScriptRoot "uia-action.ps1") @args 2>&1 | Out-String
  Write-Output ("        " + $out.Trim())
}
function Start-App {
  $exe = Join-Path $root "src\SurgeLink.App\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\SurgeLink.exe"
  if (-not (Test-Path $exe)) { throw "missing exe: $exe" }
  $p = Start-Process -FilePath $exe -PassThru
  $script:appPid = $p.Id
  Start-Sleep -Seconds $WaitSeconds
  if ($p.HasExited) { throw ("app exited with " + $p.ExitCode) }
}

Get-Process SurgeLink -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700

if (-not $NoBuild) {
  Step "Build (solution, x64)"
  Push-Location $root
  try {
    $out = & dotnet build SurgeLink.slnx -c Debug -p:Platform=x64 -v q -nologo 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
      Fail "solution build" (($out -split "`n" | Where-Object { $_ -match "error" } | Select-Object -First 12) -join "`n")
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

# ---------------------------------------------------------------------------
# Phase 1: English UI, drive the demo session end to end
# ---------------------------------------------------------------------------
Step "Launch (English UI)"
Set-Language 1
Start-App
Pass "app launched"

$shell = Dump "01-shell"
AssertContains $shell "ControlType.Window name='SurgeLink'" "window title"
AssertMissing $shell "Render error" "no render errors in shell"
AssertContains $shell "Sessions" "sidebar pane present"

Step "Open demo session"
Invoke-Action "Open demo session" "Button"
Start-Sleep -Seconds 5
$demo = Dump "02-demo"
AssertContains $demo "Connected" "demo session connected"
AssertContains $demo "Demo (offline)" "demo protocol shown in status bar"
AssertContains $demo "Monitor" "capability switcher shows Monitor"
AssertContains $demo "Files" "capability switcher shows Files"
AssertMissing $demo "Render error" "no render errors after connect"

Step "Files view"
Invoke-Action "Files" "ListItem" -Select
Start-Sleep -Seconds 3
$files = Dump "03-files"
AssertContains $files "demo.txt" "SFTP listing shows demo.txt"
AssertContains $files "Documents" "SFTP listing shows Documents"
AssertMissing $files "Render error" "no render errors in files view"

Step "Monitor view"
Invoke-Action "Monitor" "ListItem" -Select
Start-Sleep -Seconds 4
$monitor = Dump "04-monitor"
AssertContains $monitor "CPU" "monitor shows CPU"
AssertMissing $monitor "Render error" "no render errors in monitor view"

Step "Terminal view"
Invoke-Action "Terminal" "ListItem" -Select
Start-Sleep -Seconds 2
$term = Dump "05-terminal"
AssertContains $term "TerminalSurface" "terminal surface present after switching back"
AssertContains $term "demo@surge" "terminal still shows the shell prompt"
AssertMissing $term "Render error" "no render errors in terminal view"

Step "New session dialog"
Invoke-Action "New session" "Button"
Start-Sleep -Seconds 2
$dialog = Dump "06-new-session"
AssertContains $dialog "Session name" "session editor opened"
AssertContains $dialog "Host" "session editor has host field"
AssertMissing $dialog "Render error" "no render errors in dialog"
& (Join-Path $PSScriptRoot "capture-window.ps1") -ProcessId $script:appPid -OutFile (Join-Path $artifacts "ui-final.png") 2>&1 | Out-Null

# ---------------------------------------------------------------------------
# Phase 2: Chinese UI (the same build, language switched through settings)
# ---------------------------------------------------------------------------
Step "Chinese UI"
Stop-Process -Id $script:appPid -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
Set-Language 2
Start-App
Pass "app relaunched in Chinese"
$cn = Dump "07-chinese"
AssertCjk $cn 25 "Chinese text in the shell"
AssertMissing $cn "Render error" "no render errors in Chinese UI"
AssertContains $cn "SurgeLink" "window still titled SurgeLink"
& (Join-Path $PSScriptRoot "capture-window.ps1") -ProcessId $script:appPid -OutFile (Join-Path $artifacts "ui-chinese.png") 2>&1 | Out-Null
foreach ($word in @("会话", "设置", "新建")) {
  if (Select-String -Path $cn -Pattern $word -SimpleMatch -Quiet) { Pass ("Chinese wording present: " + $word) } else { Fail ("Chinese wording present: " + $word) "" }
}

# restore the machine default so the next manual run follows Windows again
Set-Language 0
Stop-Process -Id $script:appPid -Force -ErrorAction SilentlyContinue

Write-Output ""
Write-Output "================ SUMMARY ================"
Write-Output ("passed: " + $script:steps.Count)
Write-Output ("failed: " + $script:failures.Count)
foreach ($f in $script:failures) { Write-Output ("  - " + $f) }
Write-Output ("artifacts: " + $artifacts)
if ($script:failures.Count -gt 0) { exit 1 }
exit 0
