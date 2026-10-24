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
$payload = Join-Path ([IO.Path]::GetTempPath()) ('SurgeLink-payload-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $outputRoot -Force
$null = New-Item -ItemType Directory -Path $payload -Force
# WinUI self-contained native assets must be regenerated for each staging directory.
& dotnet clean (Join-Path $repoRoot 'src/SurgeLink.App/SurgeLink.App.csproj') -c Release -r "win-$Architecture" "-p:Platform=$Architecture" -m:1 -v:quiet
if ($LASTEXITCODE -ne 0) { throw 'Release clean failed.' }
& dotnet publish (Join-Path $repoRoot 'src/SurgeLink.App/SurgeLink.App.csproj') `
    -c Release -r "win-$Architecture" --self-contained true "-p:Platform=$Architecture" `
    -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true `
    -p:PublishTrimmed=false -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
    "-p:Version=$Version" -o $payload -m:1 -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
# Some WinUI native framework files reach TargetDir but are omitted from PublishDir.
# Copy the freshly cleaned/built runtime layout as well as the .NET publish assets.
$targetDir = & dotnet msbuild (Join-Path $repoRoot 'src/SurgeLink.App/SurgeLink.App.csproj') -nologo -getProperty:TargetDir -p:Configuration=Release "-p:Platform=$Architecture" "-p:RuntimeIdentifier=win-$Architecture"
if ($LASTEXITCODE -ne 0) { throw 'Cannot locate the runtime build directory.' }
Get-ChildItem -LiteralPath ($targetDir | Out-String).Trim() | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $payload -Recurse -Force
}
foreach ($required in @('SurgeLink.exe', 'SurgeLink.dll', 'SurgeLink.pri', 'Reactor.pri', 'Microsoft.UI.Xaml.Controls.pri', 'coreclr.dll', 'Microsoft.UI.Xaml.dll', 'Assets/AppIcon.ico')) {
    if (!(Test-Path -LiteralPath (Join-Path $payload $required))) { throw "Publish output is missing $required" }
}
# Keep the CLI payload isolated so its dependency/runtime files cannot overwrite the WinUI app.
& dotnet publish (Join-Path $repoRoot 'src/SurgeLink.Cli/SurgeLink.Cli.csproj') `
    -c Release -r "win-$Architecture" --self-contained true "-p:Platform=$Architecture" `
    -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false `
    "-p:Version=$Version" -o (Join-Path $payload 'cli') -m:1 -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }
if (!(Test-Path -LiteralPath (Join-Path $payload 'cli/surgelink-cli.exe'))) { throw 'CLI publish output is missing.' }
& dotnet publish (Join-Path $repoRoot 'src/SurgeLink.AgentHost/SurgeLink.AgentHost.csproj') `
    -c Release -r "win-$Architecture" --self-contained true "-p:Platform=$Architecture" `
    -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false `
    "-p:Version=$Version" -o (Join-Path $payload 'agent') -m:1 -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'MCP host publish failed.' }
if (!(Test-Path -LiteralPath (Join-Path $payload 'agent/surgelink-agent-host.exe'))) { throw 'MCP host publish output is missing.' }
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $payload 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $repoRoot 'PRIVACY.md') -Destination $payload
$revision = & git -C $repoRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot read source revision.' }
@"
SurgeLink source: https://github.com/AviderMin/SurgeLink
Build base revision: $revision
License: MIT; see LICENSE.txt.
Third-party components retain their own licenses and notices.
"@ | Set-Content -LiteralPath (Join-Path $payload 'SOURCE.txt') -Encoding utf8
Write-Output "Published payload: $payload"
if ($PublishOnly) { return }
& $IsccPath "/DAppVersion=$Version" "/DAppArchitecture=$Architecture" "/DPayloadDir=$payload" "/DOutputDir=$outputRoot" (Join-Path $repoRoot 'packaging/SurgeLink.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
$installer = Join-Path $outputRoot "SurgeLink-Setup-$Version-$Architecture.exe"
if (!(Test-Path -LiteralPath $installer)) { throw 'Installer output is missing.' }
(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash | Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
Write-Output "Installer: $installer"
