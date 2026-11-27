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

try {
    # The CLI and MCP host are self-contained .NET apps like the desktop. Publishing
    # each into its own folder shipped three complete copies of the .NET 10 runtime
    # (about 154 MB of identical bytes). They target the same runtime, so they are
    # merged into the application folder instead and load the single copy already
    # there; a collision with different content is a real conflict and fails loudly.
    function Merge-Payload([string] $source, [string] $destination) {
        $root = [IO.Path]::GetFullPath($destination).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        Get-ChildItem -LiteralPath $source -Recurse -File | ForEach-Object {
            $relative = $_.FullName.Substring(([IO.Path]::GetFullPath($source).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar).Length)
            $target = Join-Path $root $relative
            if (Test-Path -LiteralPath $target) {
                if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash) {
                    throw "Publish conflict: $relative differs between the desktop and the companion executable."
                }
                return
            }
            $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target)
            Copy-Item -LiteralPath $_.FullName -Destination $target
        }
    }

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
    # Companion executables are staged separately and merged, so a partial publish
    # never leaves a half-populated payload behind.
    $staging = Join-Path ([IO.Path]::GetTempPath()) ('SurgeLink-companions-' + [guid]::NewGuid().ToString('N'))
    try {
        foreach ($companion in @(
            @{ Project = 'src/SurgeLink.Cli/SurgeLink.Cli.csproj'; Folder = 'cli'; Exe = 'surgelink-cli.exe'; Label = 'CLI' },
            @{ Project = 'src/SurgeLink.AgentHost/SurgeLink.AgentHost.csproj'; Folder = 'agent'; Exe = 'surgelink-agent-host.exe'; Label = 'MCP host' }
        )) {
            $staged = Join-Path $staging $companion.Folder
            & dotnet publish (Join-Path $repoRoot $companion.Project) `
                -c Release -r "win-$Architecture" --self-contained true "-p:Platform=$Architecture" `
                -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false `
                "-p:Version=$Version" -o $staged -m:1 -v:minimal
            if ($LASTEXITCODE -ne 0) { throw "$($companion.Label) publish failed." }
            if (!(Test-Path -LiteralPath (Join-Path $staged $companion.Exe))) { throw "$($companion.Label) publish output is missing." }
            Merge-Payload $staged $payload
        }
    }
    finally {
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    }
    foreach ($required in @('surgelink-cli.exe', 'surgelink-agent-host.exe')) {
        if (!(Test-Path -LiteralPath (Join-Path $payload $required))) { throw "Merged payload is missing $required" }
    }
    # Remove any symbol files copied from TargetDir or NuGet runtime packs before packaging.
    $resolvedPayload = [IO.Path]::GetFullPath($payload).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    Get-ChildItem -LiteralPath $payload -Filter '*.pdb' -Recurse -File | ForEach-Object {
        if (!$_.FullName.StartsWith($resolvedPayload, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Symbol path is outside the publish payload.'
        }
        Remove-Item -LiteralPath $_.FullName -Force
    }
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
    $payloadBytes = (Get-ChildItem -LiteralPath $payload -Recurse -File | Measure-Object -Property Length -Sum).Sum
    Write-Output ("Published payload: {0} ({1:N1} MB, one shared .NET runtime)" -f $payload, ($payloadBytes / 1MB))
    if ($PublishOnly) { return }
    & $IsccPath "/DAppVersion=$Version" "/DAppArchitecture=$Architecture" "/DPayloadDir=$payload" "/DOutputDir=$outputRoot" (Join-Path $repoRoot 'packaging/SurgeLink.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
    $installer = Join-Path $outputRoot "SurgeLink-Setup-$Version-$Architecture.exe"
    if (!(Test-Path -LiteralPath $installer)) { throw 'Installer output is missing.' }
    (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash | Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
    Write-Output "Installer: $installer"
}
finally {
    if (!$PublishOnly -and (Test-Path -LiteralPath $payload)) {
        Remove-Item -LiteralPath $payload -Recurse -Force
    }
}
