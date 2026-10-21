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
  if ($count -ge $minimum) { Pass ($label + " (" + $count + " CJK characters)") } else { Fail $label ("only " + $count + " CJK characters") }
}
function Invoke-Action($name, $type, [switch]$Select) {
  $args = @{ Name = $name; Type = $type; TimeoutSeconds = 12; ProcessId = $script:appPid }
  if ($Select) { $args['Select'] = $true }
  $out = & (Join-Path $PSScriptRoot "uia-action.ps1") @args 2>&1 | Out-String
  Write-Output ("        " + $out.Trim())
}
function Get-Control($name, $type) {
  Add-Type -AssemblyName UIAutomationClient
  Add-Type -AssemblyName UIAutomationTypes
  $processCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:appPid)
  $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $processCondition)
  $nameCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
  $controls = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $nameCondition)
  foreach ($control in $controls) {
    if ($control.Current.ControlType.ProgrammaticName -eq ("ControlType." + $type)) { return $control }
  }
  throw ("Control not found: " + $name + " (" + $type + ")")
}
function AssertEnabled($name, [bool]$expected, $label) {
  $control = Get-Control $name "Button"
  if ($control.Current.IsEnabled -eq $expected) { Pass $label } else { Fail $label ("enabled=" + $control.Current.IsEnabled) }
}
function Start-App {
  $exe = Join-Path $root "src\SurgeLink.App\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\SurgeLink.exe"
  if (-not (Test-Path $exe)) { throw "missing exe: $exe" }
  $p = Start-Process -FilePath $exe -PassThru
  $script:appPid = $p.Id
  Start-Sleep -Seconds $WaitSeconds
  if ($p.HasExited) { throw ("app exited with " + $p.ExitCode) }
}

# Preserve the user's exact settings, including preferences unrelated to this run.
$settingsPath = Join-Path $appData 'settings.json'
$hadSettings = Test-Path $settingsPath
$originalSettings = if ($hadSettings) { [System.IO.File]::ReadAllBytes($settingsPath) } else { $null }
try {

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
AssertContains $shell "SidebarMonitor" "compact remote monitor present"

Step "Settings page"
Invoke-Action "Settings" "Button"
Start-Sleep -Seconds 1
$settings = Dump "00-settings"
AssertContains $settings "SettingsLanguage" "settings language control present"
AssertContains $settings "SettingsTheme" "settings theme control present"
AssertContains $settings "SettingsFontFamily" "settings terminal controls present"
AssertMissing $settings "Render error" "no render errors in settings page"

Step "Open demo session"
Invoke-Action "Open demo session" "Button"
Start-Sleep -Seconds 5
$demo = Dump "02-demo"
AssertContains $demo "Connected" "demo session connected"
AssertContains $demo "Demo (offline)" "demo protocol shown in status bar"
AssertContains $demo "Monitor" "capability switcher shows Monitor"
AssertContains $demo "Files" "capability switcher shows Files"
AssertContains $demo "SidebarMonitor" "compact monitor is live for the session"
AssertContains $demo "CPU total usage" "compact monitor shows CPU gauge"
AssertContains $demo "RAM usage" "compact monitor shows RAM gauge"
AssertContains $demo "Disk" "compact monitor shows disk throughput"
AssertMissing $demo "Render error" "no render errors after connect"

Step "Clear session filter"
& (Join-Path $PSScriptRoot "uia-action.ps1") -AutomationId "SessionFilter" -SetValue -Value "__surge_no_match__" -ProcessId $script:appPid
Start-Sleep -Milliseconds 700
$filtered = Dump "02a-filtered"
AssertContains $filtered "No matching sessions" "session filter applies"
Invoke-Action "Clear session filter" "Button"
Start-Sleep -Milliseconds 900
$cleared = Dump "02b-cleared"
AssertMissing $cleared "__surge_no_match__" "clear filter clears text and stays cleared"
AssertMissing $cleared "No matching sessions" "clear filter restores sessions"

Step "Files view"
Invoke-Action "Files" "ListItem" -Select
Start-Sleep -Seconds 3
$files = Dump "03-files"
AssertContains $files "demo.txt" "SFTP listing shows demo.txt"
AssertContains $files "Documents" "SFTP listing shows Documents"
AssertContains $files "Edit" "edit command present"
AssertMissing $files "Show hidden files" "hidden-files toggle removed"
AssertMissing $files "Render error" "no render errors in files view"

Step "Built-in file editor"
Invoke-Action "demo.txt" "ListItem" -Select
Start-Sleep -Milliseconds 400
Invoke-Action "Edit" "Button"
Start-Sleep -Seconds 2
$editor = Dump "03-editor"
AssertContains $editor "File contents" "editor shows file contents"
AssertContains $editor "Save" "editor has save action"
AssertMissing $editor "Render error" "no render errors in editor"
Invoke-Action "Cancel" "Button"
Start-Sleep -Milliseconds 500

Step "File selection and name validation"
Invoke-Action "Documents" "ListItem" -Select
Start-Sleep -Milliseconds 300
AssertEnabled "Download" $false "directory-only selection cannot download"
AssertEnabled "Delete" $true "single selection can delete"
$second = Get-Control "demo.txt" "ListItem"
$second.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).AddToSelection()
Start-Sleep -Milliseconds 300
AssertEnabled "Delete" $false "multiple selection cannot silently delete one entry"
AssertEnabled "Rename" $false "multiple selection cannot rename one entry"
Invoke-Action "New folder" "Button"
Start-Sleep -Milliseconds 500
AssertEnabled "Create" $false "empty folder name cannot submit"
& (Join-Path $PSScriptRoot "uia-action.ps1") -Name "Folder name" -Type "Edit" -SetValue -Value "../bad" -ProcessId $script:appPid
Start-Sleep -Milliseconds 300
AssertEnabled "Create" $false "folder name cannot contain a path"
& (Join-Path $PSScriptRoot "uia-action.ps1") -Name "Folder name" -Type "Edit" -SetValue -Value "ux-check" -ProcessId $script:appPid
Start-Sleep -Milliseconds 300
AssertEnabled "Create" $true "valid folder name can submit"
Invoke-Action "Create" "Button"
Start-Sleep -Milliseconds 700
$created = Dump "03a-created"
AssertContains $created "ux-check" "folder created and listing refreshed"
AssertMissing $created "Render error" "no render error after folder dialog submission"

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

Step "Session editor validation and event lifetime"
Invoke-Action "Save & connect" "Button"
Start-Sleep -Seconds 1
$invalid = Dump "06a-session-invalid"
AssertContains $invalid "Check the connection details" "invalid submit keeps the dialog open"
AssertContains $invalid "Host is required." "invalid submit shows the validation message"
AssertContains $invalid "SessionEditorPrimary" "primary button keeps its automation identity"
AssertMissing $invalid "Render error" "no render errors after invalid submit"

Invoke-Action "Cancel" "Button"
Start-Sleep -Seconds 1
$closed = Dump "06b-session-cancelled"
AssertMissing $closed "SessionEditorDialog" "cancel closes the session editor"

Invoke-Action "New session" "Button"
Start-Sleep -Seconds 2
$reopened = Dump "06c-session-reopened"
AssertContains $reopened "SessionEditorDialog" "session editor can be reopened"
AssertContains $reopened "Host" "reopened editor has the host field"
AssertMissing $reopened "Render error" "no render errors after reopening the editor"

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
foreach ($word in @(-join ([char]0x4f1a, [char]0x8bdd), -join ([char]0x8bbe, [char]0x7f6e), -join ([char]0x65b0, [char]0x5efa))) {
  if (Select-String -Path $cn -Pattern $word -SimpleMatch -Quiet) { Pass ("Chinese wording present: " + $word) } else { Fail ("Chinese wording present: " + $word) "" }
}

Stop-Process -Id $script:appPid -Force -ErrorAction SilentlyContinue

Write-Output ""
Write-Output "================ SUMMARY ================"
Write-Output ("passed: " + $script:steps.Count)
Write-Output ("failed: " + $script:failures.Count)
foreach ($f in $script:failures) { Write-Output ("  - " + $f) }
Write-Output ("artifacts: " + $artifacts)
if ($script:failures.Count -gt 0) { exit 1 }
exit 0
} finally {
  if ($script:appPid -gt 0) { Stop-Process -Id $script:appPid -Force -ErrorAction SilentlyContinue }
  if ($hadSettings) {
    [System.IO.File]::WriteAllBytes($settingsPath, $originalSettings)
  } elseif (Test-Path $settingsPath) {
    Remove-Item -LiteralPath $settingsPath
  }
}
