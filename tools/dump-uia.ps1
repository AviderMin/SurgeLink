param(
  [Parameter(Mandatory=$true)][int]$ProcessId,
  [int]$MaxDepth = 6,
  [int]$MaxNodes = 400
)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if ($null -eq $win) { Write-Error "No window for pid $ProcessId"; exit 2 }
Write-Output ("window: name='{0}' class='{1}' id='{2}'" -f $win.Current.Name, $win.Current.ClassName, $win.Current.AutomationId)
$count = 0
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
function Dump($el, $depth) {
  if ($depth -gt $MaxDepth -or $script:count -gt $MaxNodes) { return }
  $script:count++
  $pad = '  ' * $depth
  $name = $el.Current.Name
  if ($name.Length -gt 90) { $name = $name.Substring(0, 90) + '...' }
  $line = "{0}{1} name='{2}' id='{3}'" -f $pad, $el.Current.ControlType.ProgrammaticName, $name.Replace("`r", ' ').Replace("`n", ' \n '), $el.Current.AutomationId
  $val = $null
  try {
    $vp = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    if ($null -ne $vp) { $val = $vp.Current.Value }
  } catch { }
  if ($val) { $line += " value='" + ($val -replace "`r?`n", ' \n ') + "'" }
  Write-Output $line
  $child = $walker.GetFirstChild($el)
  while ($null -ne $child) {
    $next = $walker.GetNextSibling($child)
    Dump $child ($depth + 1)
    $child = $next
  }
}
Dump $win 0
Write-Output "nodes=$count"
