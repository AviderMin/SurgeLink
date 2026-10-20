param(
  [Parameter(Mandatory=$true)][string]$Project,
  [Parameter(Mandatory=$true)][string]$Agent,
  [string]$Configuration = "Debug",
  [switch]$Test,
  [string]$Filter
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$projPath = if (Test-Path $Project) { (Resolve-Path $Project).Path } else { Join-Path $root $Project }
$artifacts = Join-Path $root "artifacts\agent-build\$Agent"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

$common = @(
  "-c", $Configuration,
  "-p:ArtifactsPath=$artifacts",
  "-p:Platform=x64",
  "-v", "m",
  "-nologo"
)

if ($Test) {
  $a = @("test", $projPath) + $common
  if ($Filter) { $a += @("--filter", $Filter) }
} else {
  $a = @("build", $projPath) + $common
}
& dotnet @a 2>&1 | Out-String
exit $LASTEXITCODE
