param(
  [switch]$NoBuild,
  [int]$WaitSeconds = 9
)
# Regression check for the SFTP context menu: right clicks must work on a file
# row, on the blank space below the last row and inside an empty folder. A
# menu flyout is invisible to a plain UIA tree dump, so this script drives real
# mouse input and then looks for the flyout MenuItem nodes.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts\verify-menu"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$script:failures = New-Object System.Collections.ArrayList
$script:passes = 0
$script:appPid = 0
$script:hwnd = [IntPtr]::Zero
$appData = Join-Path $env:LOCALAPPDATA "SurgeLink"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class SurgeMenuInput {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
[SurgeMenuInput]::SetProcessDPIAware() | Out-Null

function Pass($label) { Write-Output ("  PASS  " + $label); $script:passes++ }
function Fail($label, $detail) { Write-Output ("  FAIL  " + $label); if ($detail) { Write-Output ("        " + $detail) }; [void]$script:failures.Add($label) }
function Step($name) { Write-Output ""; Write-Output "== $name ==" }

function Get-AppWindow {
  $condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:appPid)
  return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
    [System.Windows.Automation.TreeScope]::Children, $condition)
}
function Find-All($scope, $name, $type) {
  $condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $name)
  $found = $scope.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
  $out = @()
  foreach ($element in $found) {
    if (-not $type -or $element.Current.ControlType.ProgrammaticName -eq ("ControlType." + $type)) { $out += $element }
  }
  return $out
}
function Focus-App {
  [SurgeMenuInput]::ShowWindow($script:hwnd, 3) | Out-Null
  Start-Sleep -Milliseconds 200
  for ($i = 0; $i -lt 12; $i++) {
    [SurgeMenuInput]::SetForegroundWindow($script:hwnd) | Out-Null
    Start-Sleep -Milliseconds 120
    if ([SurgeMenuInput]::GetForegroundWindow() -eq $script:hwnd) { return $true }
  }
  return $false
}
function Invoke-LeftClick($x, $y, $twice) {
  [SurgeMenuInput]::SetCursorPos($x, $y) | Out-Null
  Start-Sleep -Milliseconds 110
  [SurgeMenuInput]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
  Start-Sleep -Milliseconds 60
  [SurgeMenuInput]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
  if ($twice) {
    Start-Sleep -Milliseconds 120
    [SurgeMenuInput]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [SurgeMenuInput]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
  }
  Start-Sleep -Milliseconds 250
}
function Invoke-RightClick($x, $y) {
  [SurgeMenuInput]::SetCursorPos($x, $y) | Out-Null
  Start-Sleep -Milliseconds 110
  [SurgeMenuInput]::mouse_event(0x0008, 0, 0, 0, [IntPtr]::Zero)
  Start-Sleep -Milliseconds 60
  [SurgeMenuInput]::mouse_event(0x0010, 0, 0, 0, [IntPtr]::Zero)
  Start-Sleep -Milliseconds 650
}
function Get-MenuItems {
  $items = @{}
  $condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:appPid)
  $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
    [System.Windows.Automation.TreeScope]::Children, $condition)
  foreach ($window in $windows) {
    $all = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
      [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($element in $all) {
      if ($element.Current.ControlType.ProgrammaticName -eq "ControlType.MenuItem") {
        $items[$element.Current.Name] = $element
      }
    }
  }
  return $items
}
function Close-Menu { [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 900 }
function Assert-Menu($expected, $label) {
  $items = Get-MenuItems
  if ($items.ContainsKey($expected)) { Pass $label }
  else { Fail $label ("menu items: " + (($items.Keys | Sort-Object) -join ", ")) }
}
function Invoke-MenuItem($name) {
  $items = Get-MenuItems
  if (-not $items.ContainsKey($name)) { return $false }
  $items[$name].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 600
  return $true
}

$settingsPath = Join-Path $appData "settings.json"
$hadSettings = Test-Path $settingsPath
$originalSettings = if ($hadSettings) { [System.IO.File]::ReadAllBytes($settingsPath) } else { $null }
try {

if (-not $NoBuild) {
  Step "Build (app, x64)"
  $out = & dotnet build (Join-Path $root "src/SurgeLink.App/SurgeLink.App.csproj") -c Debug -p:Platform=x64 -v q -nologo 2>&1 | Out-String
  if ($LASTEXITCODE -eq 0) { Pass "app build" } else { Fail "app build" (($out -split "\r?\n" | Where-Object { $_ -match "error" } | Select-Object -First 5) -join " | "); exit 1 }
}

Step "Launch (English UI)"
New-Item -ItemType Directory -Force -Path $appData | Out-Null
$settingsJson = '{"language":1}'
[System.IO.File]::WriteAllText($settingsPath, $settingsJson)
$exe = Join-Path $root "src\SurgeLink.App\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\SurgeLink.exe"
if (-not (Test-Path $exe)) { throw "missing exe: $exe" }
Get-Process SurgeLink -ErrorAction SilentlyContinue | ForEach-Object { try { if ($_.Path -eq $exe) { Stop-Process -Id $_.Id -Force } } catch { } }
Start-Sleep -Milliseconds 500
$process = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds $WaitSeconds
if ($process.HasExited) { throw ("app exited with " + $process.ExitCode) }
$script:appPid = $process.Id
$process.Refresh()
$script:hwnd = $process.MainWindowHandle
if (Focus-App) { Pass "app is in the foreground" } else { Fail "app is in the foreground" "mouse input would go to another window" }

Step "Open the demo file manager"
& (Join-Path $PSScriptRoot "uia-action.ps1") -Name "Demo session" -Type "Button" -ProcessId $script:appPid | Out-Null
Start-Sleep -Seconds 3
& (Join-Path $PSScriptRoot "uia-action.ps1") -Name "Files" -Type "ListItem" -Select -ProcessId $script:appPid | Out-Null
Start-Sleep -Seconds 4
Focus-App | Out-Null

$window = Get-AppWindow
$row = (Find-All $window "demo.txt" "ListItem")[0]
if ($null -eq $row) { throw "demo.txt row not found; is the demo file system available?" }
$rowRect = $row.Current.BoundingRectangle
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$listNode = $row
while ($null -ne $listNode -and $listNode.Current.ControlType.ProgrammaticName -ne "ControlType.List") { $listNode = $walker.GetParent($listNode) }
$listRect = $listNode.Current.BoundingRectangle

Step "Right click on a file row"
Invoke-RightClick ([int]($rowRect.X + 120)) ([int]($rowRect.Y + $rowRect.Height / 2))
Assert-Menu "Copy" "row menu opens"
if (Invoke-MenuItem "Copy") { Pass "Copy command captured the row" } else { Fail "Copy command captured the row" "menu item missing" }
Close-Menu

Step "Right click on the blank space below the rows"
Invoke-RightClick ([int]($listRect.X + 600)) ([int]($listRect.Y + $listRect.Height * 0.9))
Assert-Menu "Paste into this folder" "blank space offers Paste into this folder"
Close-Menu

Step "Right click inside an empty folder"
& (Join-Path $PSScriptRoot "uia-action.ps1") -Name "New folder" -Type "Button" -ProcessId $script:appPid | Out-Null
Start-Sleep -Milliseconds 900
& (Join-Path $PSScriptRoot "uia-action.ps1") -Name "Folder name" -Type "Edit" -SetValue -Value "menu-check" -ProcessId $script:appPid | Out-Null
Start-Sleep -Milliseconds 300
& (Join-Path $PSScriptRoot "uia-action.ps1") -Name "Create" -Type "Button" -ProcessId $script:appPid | Out-Null
Start-Sleep -Seconds 2
Focus-App | Out-Null
$window = Get-AppWindow
$folderRow = (Find-All $window "menu-check" "ListItem")[0]
$folderRect = $folderRow.Current.BoundingRectangle
Invoke-LeftClick ([int]($folderRect.X + 120)) ([int]($folderRect.Y + $folderRect.Height / 2)) $true
Start-Sleep -Seconds 2
Focus-App | Out-Null
$window = Get-AppWindow
$emptyLabel = Find-All $window "This folder is empty" "Text"
if ($emptyLabel.Count -gt 0) { Pass "empty folder shows its placeholder" } else { Fail "empty folder shows its placeholder" "placeholder text missing" }
$emptyRect = if ($emptyLabel.Count -gt 0) { $emptyLabel[0].Current.BoundingRectangle } else { $listRect }
$emptyX = [int]($emptyRect.X + $emptyRect.Width / 2)
$emptyY = [int]($emptyRect.Y + 260)
Invoke-RightClick $emptyX $emptyY
Assert-Menu "Paste into this folder" "empty folder answers a right click away from the placeholder"
$items = Get-MenuItems
if ($items.ContainsKey("Paste into this folder") -and $items["Paste into this folder"].Current.IsEnabled) {
  Pass "paste is offered in the empty folder"
  if (Invoke-MenuItem "Paste into this folder") { Pass "paste command ran" } else { Fail "paste command ran" "menu item missing" }
  Start-Sleep -Seconds 2
  $window = Get-AppWindow
  if ((Find-All $window "demo.txt" "ListItem").Count -gt 0) { Pass "pasted file appears in the empty folder" }
  else { Fail "pasted file appears in the empty folder" "demo.txt not listed after paste" }
} else {
  Fail "paste is offered in the empty folder" "Paste into this folder missing or disabled"
}
Close-Menu

& (Join-Path $PSScriptRoot "capture-window.ps1") -ProcessId $script:appPid -OutFile (Join-Path $artifacts "ui-files-menu.png") 2>&1 | Out-Null

Write-Output ""
Write-Output "================ SUMMARY ================"
Write-Output ("passed: " + $script:passes)
Write-Output ("failed: " + $script:failures.Count)
foreach ($failure in $script:failures) { Write-Output ("  - " + $failure) }
Write-Output ("artifacts: " + $artifacts)
if ($script:failures.Count -gt 0) { exit 1 }
exit 0

} finally {
  if ($script:appPid -gt 0) { Stop-Process -Id $script:appPid -Force -ErrorAction SilentlyContinue }
  if ($hadSettings) { [System.IO.File]::WriteAllBytes($settingsPath, $originalSettings) }
  elseif (Test-Path $settingsPath) { Remove-Item -LiteralPath $settingsPath }
}
