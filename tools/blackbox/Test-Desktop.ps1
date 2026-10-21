param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [switch]$ConfirmTestProfile,
    [int]$TimeoutSeconds = 15,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../../artifacts/blackbox/desktop')
)
$ErrorActionPreference = 'Stop'
if (!$ConfirmTestProfile) { throw 'Run under a dedicated Windows test account/VM and supply -ConfirmTestProfile. Validation defects could otherwise create test profiles in real app data.' }
if (Get-Process SurgeLink -ErrorAction SilentlyContinue) { throw 'Close existing SurgeLink instances first. This script never attaches to or kills user instances.' }
$ExecutablePath = (Resolve-Path -LiteralPath $ExecutablePath).Path
. (Join-Path $PSScriptRoot 'Report.ps1')
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$script:process = $null
$script:window = $null
$script:editorOpen = $false

function Wait-BB([scriptblock]$Probe, [string]$Description) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        if ($script:process -and $script:process.HasExited) { throw "Application exited: $($script:process.ExitCode)" }
        $value = & $Probe
        if ($value) { return $value }
        Start-Sleep -Milliseconds 150
    } while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    throw "Timed out: $Description"
}
function Find-BB([string]$Id = '', [string]$Name = '', [string]$Type = '') {
    if (!$script:window) { return $null }
    $elements = $script:window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    $foundElements = @()
    foreach ($element in $elements) {
        try {
            if ($Id -and $element.Current.AutomationId -ne $Id) { continue }
            if ($Name -and $element.Current.Name -notmatch $Name) { continue }
            if ($Type -and $element.Current.ControlType.ProgrammaticName -ne "ControlType.$Type") { continue }
            $foundElements += $element
        } catch [Windows.Automation.ElementNotAvailableException] { continue }
    }
    if ($foundElements.Count -gt 1) { throw "Ambiguous locator: id=$Id name=$Name type=$Type ($($foundElements.Count) matches)" }
    if ($foundElements.Count -eq 1) { return $foundElements[0] }
    return $null
}
function Get-BB([string]$Id = '', [string]$Name = '', [string]$Type = '') {
    return Wait-BB { Find-BB -Id $Id -Name $Name -Type $Type } "control id=$Id name=$Name type=$Type"
}
function Click-BB($Element) {
    Assert-BB $Element.Current.IsEnabled 'Control is disabled.'
    $pattern = $Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}
function Set-BB($Element, [string]$Value) {
    $pattern = $Element.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($Value)
}
function Assert-NoRenderError {
    $errorControl = Find-BB -Name 'Render error' -Type Text
    Assert-BB ($null -eq $errorControl) 'Reactor render error visible.'
}
function Wait-Gone([string]$Id) {
    $null = Wait-BB { if (!(Find-BB -Id $Id)) { return $true }; return $false } "$Id closes"
}
function Need-Window { if (!$script:window) { throw 'SKIP: main window unavailable.' } }
function Need-Editor {
    Need-Window
    if (!$script:editorOpen -or !(Find-BB -Id SessionEditorDialog)) { throw 'SKIP: session editor unavailable after previous failure.' }
}

try {
    Invoke-BBCase 'UI001' 'Application starts and exposes its own main window' {
        $script:process = Start-Process -FilePath $ExecutablePath -WorkingDirectory (Split-Path $ExecutablePath) -PassThru
        $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$script:process.Id)
        $script:window = Wait-BB { [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,$condition) } 'main window'
    }
    Invoke-BBCase 'UI002' 'Main title is SurgeLink' { Need-Window; Assert-BB ($script:window.Current.Name -eq 'SurgeLink') 'Incorrect window title.' }
    Invoke-BBCase 'UI003' 'Shell contains no render error' { Need-Window; Assert-NoRenderError }
    Invoke-BBCase 'UI004' 'Settings opens as native dialog' {
        Need-Window; Click-BB (Get-BB -Name '^(Settings|设置)$' -Type Button); $null = Get-BB -Id SettingsDialog
    }
    Invoke-BBCase 'UI005' 'Settings exposes language input' { Need-Window; $null = Get-BB -Id SettingsLanguage -Type ComboBox }
    Invoke-BBCase 'UI006' 'Settings exposes theme input' { Need-Window; $null = Get-BB -Id SettingsTheme -Type ComboBox }
    Invoke-BBCase 'UI007' 'Settings exposes terminal font input' { Need-Window; $null = Get-BB -Id SettingsFontFamily -Type ComboBox }
    Invoke-BBCase 'UI008' 'Settings Save and Cancel are accessible and enabled' {
        Need-Window
        foreach ($id in @('SettingsPrimary','SettingsClose')) { $button = Get-BB -Id $id -Type Button; Assert-BB $button.Current.IsEnabled "$id disabled" }
    }
    Invoke-BBCase 'UI009' 'Settings Cancel dismisses dialog' { Need-Window; Click-BB (Get-BB -Id SettingsClose); Wait-Gone SettingsDialog }
    Invoke-BBCase 'UI010' 'Settings can reopen and dismiss without error' {
        Need-Window; Click-BB (Get-BB -Name '^(Settings|设置)$' -Type Button)
        $null = Get-BB -Id SettingsDialog
        Click-BB (Get-BB -Id SettingsClose); Wait-Gone SettingsDialog; Assert-NoRenderError
    }
    Invoke-BBCase 'UI011' 'About opens as native dialog' {
        Need-Window; Click-BB (Get-BB -Name '^(About SurgeLink|关于 SurgeLink)$' -Type Button); $null = Get-BB -Id AboutDialog
    }
    Invoke-BBCase 'UI012' 'About shows installed executable version' {
        Need-Window
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($ExecutablePath)
        $expected = '{0}.{1}.{2}' -f $version.FileMajorPart,$version.FileMinorPart,$version.FileBuildPart
        $null = Get-BB -Name ('^(Version|版本)\s+' + [regex]::Escape($expected) + '$') -Type Text
    }
    Invoke-BBCase 'UI013' 'About Close dismisses dialog' { Need-Window; Click-BB (Get-BB -Id AboutPrimary); Wait-Gone AboutDialog }
    Invoke-BBCase 'UI014' 'New session editor opens' {
        Need-Window; Click-BB (Get-BB -Id NewSessionButton); $null = Get-BB -Id SessionEditorDialog; $script:editorOpen = $true
    }
    Invoke-BBCase 'UI015' 'Empty host is rejected and editor stays open' {
        Need-Editor
        Set-BB (Get-BB -Name '^(Host|主机)$' -Type Edit) ''
        Click-BB (Get-BB -Id SessionEditorSecondary)
        $null = Get-BB -Name 'Host is required|请.*主机|主机.*必|主机不能为空' -Type Text
        $null = Get-BB -Id SessionEditorDialog
    }
    $invalidPorts = @('0','-1','65536','abc','22.5')
    for ($index = 0; $index -lt $invalidPorts.Count; $index++) {
        $port = $invalidPorts[$index]
        Invoke-BBCase ('UI{0:000}' -f (16 + $index)) "Invalid port '$port' is rejected" {
            Need-Editor
            Set-BB (Get-BB -Name '^(Host|主机)$' -Type Edit) 'blackbox.invalid'
            Set-BB (Get-BB -Name '^(Port|端口)$' -Type Edit) $port
            Click-BB (Get-BB -Id SessionEditorSecondary)
            $null = Get-BB -Name '65535' -Type Text
            $null = Get-BB -Id SessionEditorDialog
        }
    }
    Invoke-BBCase 'UI021' 'Session editor Cancel closes invalid draft' {
        Need-Editor; Click-BB (Get-BB -Id SessionEditorClose); Wait-Gone SessionEditorDialog; $script:editorOpen = $false
    }
    $filterValue = '__bb_no_match_' + [Guid]::NewGuid().ToString('N')
    Invoke-BBCase 'UI022' 'Session filter accepts test query' {
        Need-Window
        $filter = Get-BB -Id SessionFilter -Type Edit
        Set-BB $filter $filterValue
        $null = Wait-BB { $pattern = (Get-BB -Id SessionFilter).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern); $pattern.Current.Value -eq $filterValue } 'filter value'
    }
    Invoke-BBCase 'UI023' 'Session filter can be cleared' {
        Need-Window; Set-BB (Get-BB -Id SessionFilter -Type Edit) ''
        $null = Wait-BB { $pattern = (Get-BB -Id SessionFilter).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern); $pattern.Current.Value -eq '' } 'empty filter'
    }
    Invoke-BBCase 'UI024' 'Application survives dialog and validation sequence' { Need-Window; Assert-BB (!$script:process.HasExited) 'Application exited.'; Assert-NoRenderError }
    Invoke-BBCase 'UI025' 'Main window closes cleanly' {
        Need-Window
        Assert-BB ($script:process.CloseMainWindow()) 'Main window did not accept close.'
        Assert-BB ($script:process.WaitForExit($TimeoutSeconds * 1000)) 'Application did not exit in time.'
        Assert-BB ($script:process.ExitCode -eq 0) "Nonzero exit: $($script:process.ExitCode)"
    }
}
finally {
    # Only clean up the exact process launched by this script.
    if ($script:process -and !$script:process.HasExited) {
        $null = $script:process.CloseMainWindow()
        if (!$script:process.WaitForExit(3000)) { Stop-Process -Id $script:process.Id -Force }
    }
}
$ok = Export-BBReport -OutputDirectory $OutputDirectory -Suite 'Desktop' -Context @{Executable=$ExecutablePath;TimeoutSeconds=$TimeoutSeconds;Profile='Dedicated test account required'}
if (!$ok) { exit 1 }
