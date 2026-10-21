param(
    [ValidateSet('Package','Desktop','All')][string]$Suite = 'Package',
    [string]$ZipPath = (Join-Path $PSScriptRoot '../../artifacts/appx/SurgeLink_1.0.2_x64.zip'),
    [string]$ExpectedVersion = '1.0.2.0',
    [string]$ExecutablePath,
    [switch]$ConfirmTestProfile,
    [switch]$ListCases,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot ('../../artifacts/blackbox/run-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6)))
)
$ErrorActionPreference = 'Stop'
$catalog = @(Get-Content (Join-Path $PSScriptRoot '../../tests/blackbox/cases.json') -Raw | ConvertFrom-Json)
if ($ListCases) { $catalog | Select-Object Id,Module,Priority,Automation,Expected; return }
if ($Suite -in @('Desktop','All') -and (!$ExecutablePath -or !$ConfirmTestProfile)) {
    throw 'Desktop/All requires -ExecutablePath and -ConfirmTestProfile under a dedicated test account/VM.'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Force -Path $OutputDirectory
$runner = (Get-Command pwsh.exe -ErrorAction Stop).Source
$exitCode = 0
$actual = @{}
foreach ($selected in @('Package','Desktop')) {
    if ($Suite -ne 'All' -and $Suite -ne $selected) { continue }
    $suiteDirectory = Join-Path $OutputDirectory $selected.ToLowerInvariant()
    $arguments = @('-NoProfile','-File',(Join-Path $PSScriptRoot "Test-$selected.ps1"),'-OutputDirectory',$suiteDirectory)
    if ($selected -eq 'Package') { $arguments += @('-ZipPath',$ZipPath,'-ExpectedVersion',$ExpectedVersion) }
    else { $arguments += @('-ExecutablePath',$ExecutablePath,'-ConfirmTestProfile') }
    & $runner @arguments
    if ($LASTEXITCODE -ne 0) { $exitCode = 1 }
    $resultPath = Join-Path $suiteDirectory 'results.json'
    if (Test-Path -LiteralPath $resultPath) {
        $report = Get-Content $resultPath -Raw | ConvertFrom-Json
        foreach ($row in $report.Results) { $actual[$row.Id] = $row }
    }
    else {
        $exitCode = 1
        Write-Warning "$selected failed before producing a report; cases remain NotRun. Check the command output."
    }
}
$coverage = foreach ($case in $catalog) {
    $row = $actual[$case.Id]
    [pscustomobject]@{
        Id=$case.Id; Module=$case.Module; Priority=$case.Priority; Automation=$case.Automation
        Status=if ($row) { $row.Status } else { 'NotRun' }
        Detail=if ($row) { $row.Detail } else { 'Not executed in this run.' }
        Expected=$case.Expected
    }
}
$coverage | Export-Csv (Join-Path $OutputDirectory 'coverage.csv') -NoTypeInformation -Encoding UTF8
$summary = [ordered]@{
    Suite=$Suite; DesignedCases=$catalog.Count; Executed=$actual.Count
    Passed=@($coverage | Where-Object Status -eq Passed).Count
    Failed=@($coverage | Where-Object Status -eq Failed).Count
    Skipped=@($coverage | Where-Object Status -eq Skipped).Count
    NotRun=@($coverage | Where-Object Status -eq NotRun).Count
    ExitCode=$exitCode; OutputDirectory=$OutputDirectory
}
$summary | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'summary.json') -Encoding UTF8
$summary | ConvertTo-Json | Write-Host
exit $exitCode
