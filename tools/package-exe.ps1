param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version,
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64',
    [string]$IsccPath,
    [switch]$PublishOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (!$Version) {
    [xml]$properties = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw
    $Version = @($properties.Project.PropertyGroup.Version | Where-Object { $_ })[0]
}
$null = [version]::Parse($Version)
if (!$PublishOnly) {
    if (!$IsccPath) {
        $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($compiler) { $IsccPath = $compiler.Source }
        else {
            foreach ($programRoot in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, (Join-Path $env:LOCALAPPDATA 'Programs'))) {
                if (!$programRoot) { continue }
                foreach ($major in @(7, 6)) {
                    $candidate = Join-Path $programRoot "Inno Setup $major/ISCC.exe"
                    if (Test-Path -LiteralPath $candidate) { $IsccPath = $candidate; break }
                }
                if ($IsccPath) { break }
            }
        }
    }
    if (!$IsccPath -or !(Test-Path -LiteralPath $IsccPath)) {
        throw 'Inno Setup compiler not found. Install Inno Setup 6.3+ from https://jrsoftware.org/isinfo.php, pass -IsccPath, or use -PublishOnly to prepare the payload.'
    }
}
$outputRoot = Join-Path $repoRoot "artifacts/installer/$Architecture"
# A fresh staging directory prevents files from earlier releases leaking into the installer.
$payload = Join-Path $outputRoot ('payload-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $payload -Force
& dotnet publish (Join-Path $repoRoot 'src/SurgeLink.App/SurgeLink.App.csproj') `
    -c Release -r "win-$Architecture" --self-contained true "-p:Platform=$Architecture" `
    -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true `
    -p:PublishTrimmed=false -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
    "-p:Version=$Version" -o $payload -m:1 -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
foreach ($required in @('SurgeLink.exe', 'SurgeLink.dll', 'Reactor.pri', 'Microsoft.UI.Xaml.Controls.pri', 'coreclr.dll', 'Assets/AppIcon.ico')) {
    if (!(Test-Path -LiteralPath (Join-Path $payload $required))) { throw "Publish output is missing $required" }
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $payload 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $repoRoot 'PRIVACY.md') -Destination $payload
$revision = & git -C $repoRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot read source revision.' }
@"
SurgeLink source: https://github.com/AviderMin/SurgeLink
Build base revision: $revision
License: GNU GPL version 2; see LICENSE.txt.
Publish the complete corresponding release source, including any local changes.
"@ | Set-Content -LiteralPath (Join-Path $payload 'SOURCE.txt') -Encoding utf8
Write-Output "Published payload: $payload"
if ($PublishOnly) { return }
& $IsccPath "/DAppVersion=$Version" "/DAppArchitecture=$Architecture" "/DPayloadDir=$payload" "/DOutputDir=$outputRoot" (Join-Path $repoRoot 'packaging/SurgeLink.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
$installer = Join-Path $outputRoot "SurgeLink-Setup-$Version-$Architecture.exe"
if (!(Test-Path -LiteralPath $installer)) { throw 'Installer output is missing.' }
(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash | Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
Write-Output "Installer: $installer"
