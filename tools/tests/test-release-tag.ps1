$ErrorActionPreference = 'Stop'
$global:releaseTagTest = @{}
$global:releaseTagTest.target = 'a' * 40
$global:releaseTagTest.other = 'b' * 40

# Stub GitHub CLI: all scenarios are offline and cannot publish anything.
function gh {
    $global:LASTEXITCODE = 0
    $global:releaseTagTest.calls.Add(($args -join ' '))
    if ($args -contains 'POST') {
        if ($global:releaseTagTest.failCreate) { $global:LASTEXITCODE = 1; return }
        $global:releaseTagTest.remote = $global:releaseTagTest.target
        return '{}'
    }
    if (($args -join ' ') -match '/git/tags/') {
        return (@{ object = @{ type = 'commit'; sha = $global:releaseTagTest.remote } } | ConvertTo-Json -Depth 5 -Compress)
    }
    $refs = @(@{ ref = 'refs/tags/v1.2.30'; object = @{ type = 'commit'; sha = $global:releaseTagTest.other } })
    if ($global:releaseTagTest.remote) {
        $refs += @{ ref = 'refs/tags/v1.2.3'; object = @{
            type = $(if ($global:releaseTagTest.annotated) { 'tag' } else { 'commit' }); sha = $global:releaseTagTest.remote
        } }
    }
    return ConvertTo-Json -InputObject $refs -Depth 5 -Compress
}

foreach ($scenario in @('missing', 'existing', 'annotated', 'conflict', 'create-failure')) {
    $global:releaseTagTest.calls = [Collections.Generic.List[string]]::new()
    $global:releaseTagTest.remote = switch ($scenario) {
        'existing' { $global:releaseTagTest.target }
        'annotated' { $global:releaseTagTest.target }
        'conflict' { $global:releaseTagTest.other }
        default { $null }
    }
    $global:releaseTagTest.annotated = $scenario -eq 'annotated'
    $global:releaseTagTest.failCreate = $scenario -eq 'create-failure'
    $failed = $false
    try {
        & "$PSScriptRoot/../ensure-release-tag.ps1" -Repository 'owner/repo' -Tag 'v1.2.3' -Sha $global:releaseTagTest.target
    }
    catch { $failed = $true }
    if ($failed -ne ($scenario -in @('conflict', 'create-failure'))) { throw "Unexpected result: $scenario" }
    $posts = @($global:releaseTagTest.calls | Where-Object { $_ -match 'POST' }).Count
    if ($posts -ne $(if ($scenario -in @('missing', 'create-failure')) { 1 } else { 0 })) {
        throw "Unexpected tag mutation: $scenario"
    }
    Write-Output "PASS: $scenario"
}

Remove-Variable releaseTagTest -Scope Global
# The last scenario intentionally simulates a failed native command. Actions
# appends `exit $LASTEXITCODE` to its wrapper, so clear the expected test failure.
$global:LASTEXITCODE = 0
