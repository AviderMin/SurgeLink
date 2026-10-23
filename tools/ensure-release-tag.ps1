param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$Sha
)

$ErrorActionPreference = 'Stop'

function Get-ReleaseTagCommit {
    # Match exactly: the API also returns tags whose names merely share a prefix.
    $refs = gh api "repos/$Repository/git/matching-refs/tags/$Tag" | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the remote release tag.' }
    $reference = @($refs | Where-Object { $_.ref -ceq "refs/tags/$Tag" })
    if ($reference.Count -eq 0) { return $null }
    $object = $reference[0].object
    for ($depth = 0; $depth -lt 16; $depth++) {
        if ($object.type -eq 'commit') { return $object.sha }
        if ($object.type -ne 'tag') { throw 'The release tag does not resolve to a commit.' }
        $annotatedTag = gh api "repos/$Repository/git/tags/$($object.sha)" | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the annotated release tag.' }
        $object = $annotatedTag.object
    }
    throw 'The release tag has too many annotation layers.'
}

$existing = Get-ReleaseTagCommit
if ($existing -and $existing -ne $Sha) {
    throw 'The remote release tag points to another commit; refusing to move it.'
}
if (!$existing) {
    gh api --method POST "repos/$Repository/git/refs" -f "ref=refs/tags/$Tag" -f "sha=$Sha" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Release tag creation failed; the release must remain a draft.' }
}
if ((Get-ReleaseTagCommit) -ne $Sha) { throw 'The remote release tag does not match the build commit.' }
