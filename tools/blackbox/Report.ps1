Set-StrictMode -Version Latest
$script:Results = [System.Collections.Generic.List[object]]::new()

function Invoke-BBCase {
    param([string]$Id, [string]$Name, [scriptblock]$Test)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $status = 'Passed'
    $detail = ''
    try { & $Test | Out-Null }
    catch {
        $detail = $_.Exception.Message
        $status = if ($detail.StartsWith('SKIP:')) { 'Skipped' } else { 'Failed' }
    }
    $watch.Stop()
    $script:Results.Add([pscustomobject]@{
        Id = $Id; Name = $Name; Status = $status; Seconds = $watch.Elapsed.TotalSeconds; Detail = $detail
    })
    Write-Host ("[{0}] {1} {2} {3}" -f $status, $Id, $Name, $detail)
}

function Assert-BB {
    param([bool]$Condition, [string]$Message)
    if (!$Condition) { throw $Message }
}

function Export-BBReport {
    param([string]$OutputDirectory, [string]$Suite, [hashtable]$Context = @{})
    $null = New-Item -ItemType Directory -Force -Path $OutputDirectory
    $items = @($script:Results.ToArray())
    $summary = [ordered]@{
        Suite = $Suite; GeneratedAt = [DateTimeOffset]::Now.ToString('o'); Context = $Context
        Total = $items.Count
        Passed = @($items | Where-Object Status -eq 'Passed').Count
        Failed = @($items | Where-Object Status -eq 'Failed').Count
        Skipped = @($items | Where-Object Status -eq 'Skipped').Count
        Results = $items
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $OutputDirectory 'results.json') -Encoding UTF8
    $items | Export-Csv (Join-Path $OutputDirectory 'results.csv') -NoTypeInformation -Encoding UTF8
    $encode = { param($value) [Net.WebUtility]::HtmlEncode([string]$value) }
    $rows = foreach ($item in $items) {
        '<tr><td>{0}</td><td>{1}</td><td>{2}</td><td>{3}</td></tr>' -f (& $encode $item.Id), (& $encode $item.Name), $item.Status, (& $encode $item.Detail)
    }
    @"
<!doctype html><html lang="en"><meta charset="utf-8"><title>SurgeLink $Suite</title>
<style>body{font:15px system-ui;margin:32px}table{border-collapse:collapse;width:100%}td,th{border:1px solid #bbb;padding:8px;text-align:left}th{background:#eee}</style>
<h1>SurgeLink $Suite</h1><p>Total $($summary.Total) / Passed $($summary.Passed) / Failed $($summary.Failed) / Skipped $($summary.Skipped)</p>
<table><tr><th>ID</th><th>Case</th><th>Status</th><th>Detail</th></tr>$($rows -join "`n")</table></html>
"@ | Set-Content (Join-Path $OutputDirectory 'report.html') -Encoding UTF8
    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Indent = $true
    $writer = [Xml.XmlWriter]::Create((Join-Path $OutputDirectory 'junit.xml'), $settings)
    try {
        $writer.WriteStartDocument()
        $writer.WriteStartElement('testsuite')
        $writer.WriteAttributeString('name', $Suite)
        $writer.WriteAttributeString('tests', [string]$summary.Total)
        $writer.WriteAttributeString('failures', [string]$summary.Failed)
        $writer.WriteAttributeString('skipped', [string]$summary.Skipped)
        foreach ($item in $items) {
            $writer.WriteStartElement('testcase')
            $writer.WriteAttributeString('classname', "SurgeLink.BlackBox.$Suite")
            $writer.WriteAttributeString('name', "$($item.Id) $($item.Name)")
            $writer.WriteAttributeString('time', $item.Seconds.ToString('F3', [Globalization.CultureInfo]::InvariantCulture))
            if ($item.Status -ne 'Passed') {
                $tag = if ($item.Status -eq 'Skipped') { 'skipped' } else { 'failure' }
                $writer.WriteElementString($tag, $item.Detail)
            }
            $writer.WriteEndElement()
        }
        $writer.WriteEndElement()
        $writer.WriteEndDocument()
    }
    finally { $writer.Dispose() }
    Write-Host "Reports: $OutputDirectory"
    return ($summary.Failed -eq 0 -and $summary.Passed -gt 0)
}
