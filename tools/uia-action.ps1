param(
  [string]$Name,
  [string]$AutomationId,
  [string]$Type = "Button",
  [switch]$SetValue,
  [switch]$Select,
  [string]$Value = "",
  [int]$TimeoutSeconds = 10,
  [int]$Index = 0,
  [int]$ProcessId = 0
)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
if ($ProcessId -gt 0) {
  $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
  if (-not $proc) { Write-Output "NO PROCESS $ProcessId"; exit 2 }
} else {
  $procs = Get-Process SurgeLink -ErrorAction SilentlyContinue
  if (-not $procs) { Write-Output "NO RUNNING INSTANCE"; exit 2 }
  $proc = $procs | Select-Object -First 1
}
$rootEl = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $rootEl.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if ($null -eq $win) { Write-Output "window not found"; exit 2 }
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$script:matches = New-Object System.Collections.ArrayList
function FindIn($element) {
  $elementName = $element.Current.Name
  $elementId = $element.Current.AutomationId
  $typeName = $element.Current.ControlType.ProgrammaticName
  $match = $false
  if ($AutomationId) {
    if ($elementId -eq $AutomationId) { $match = $true }
  } elseif ($Name) {
    if ($elementName -eq $Name -and (-not $Type -or $typeName -eq ("ControlType." + $Type))) { $match = $true }
  }
  if ($match) { [void]$script:matches.Add($element) }
  $child = $walker.GetFirstChild($element)
  while ($null -ne $child) {
    $next = $walker.GetNextSibling($child)
    FindIn $child
    $child = $next
  }
}
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ($script:matches.Count -le $Index -and (Get-Date) -lt $deadline) {
  $script:matches.Clear()
  FindIn $win
  if ($script:matches.Count -le $Index) { Start-Sleep -Milliseconds 400 }
}
if ($script:matches.Count -le $Index) { Write-Output "NOT FOUND name='$Name' id='$AutomationId' type='$Type'"; exit 3 }
$target = $script:matches[$Index]
if ($SetValue) {
  $pattern = $target.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
  $pattern.SetValue($Value)
  Write-Output "SETVALUE name='$($target.Current.Name)' value='$Value'"
} elseif ($Select) {
  $pattern = $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
  $pattern.Select()
  Write-Output "SELECTED name='$($target.Current.Name)'"
} else {
  $pattern = $target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
  $pattern.Invoke()
  Write-Output "INVOKED name='$($target.Current.Name)' type='$($target.Current.ControlType.ProgrammaticName)'"
}