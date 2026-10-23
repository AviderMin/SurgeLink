param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = '1.0.2',
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$parsedVersion = [version]$Version
$packageVersion = if ($parsedVersion.Revision -lt 0) { "$Version.0" } else { $Version }
$repoRoot = Split-Path $PSScriptRoot -Parent
$outputRoot = Join-Path $repoRoot 'artifacts/appx'
if ($Architecture -ne 'x64') { $outputRoot = Join-Path $outputRoot $Architecture }
$payload = Join-Path $outputRoot 'payload'
$null = New-Item -ItemType Directory -Force -Path $outputRoot

if (!$SkipPublish) {
    & (Join-Path $PSScriptRoot 'make-icon.ps1')
    & dotnet publish (Join-Path $repoRoot 'src/SurgeLink.App/SurgeLink.App.csproj') `
        -c Release -r "win-$Architecture" --self-contained true "-p:Platform=$Architecture" `
        -p:WindowsAppSDKSelfContained=true -p:UseSharedCompilation=false `
        -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version" -o $payload -m:1 -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
}
if (!(Test-Path (Join-Path $payload 'SurgeLink.exe'))) { throw 'Publish output is missing.' }

# Include the project license, privacy policy and source location in binary releases.
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $payload 'LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'PRIVACY.md') -Destination (Join-Path $payload 'PRIVACY.md') -Force
$sourceRevision = & git -C $repoRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine source revision.' }
@"
SurgeLink source: https://github.com/AviderMin/SurgeLink
Build base revision: $sourceRevision
License: MIT; see LICENSE.txt.
Third-party components retain their own licenses and notices.
"@ | Set-Content -LiteralPath (Join-Path $payload 'SOURCE.txt') -Encoding utf8
# SkipPublish may reuse a payload from an earlier build containing source-path symbols.
$resolvedPayload = [IO.Path]::GetFullPath($payload).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
Get-ChildItem -LiteralPath $payload -Filter '*.pdb' -Recurse -File | ForEach-Object {
    if (!$_.FullName.StartsWith($resolvedPayload, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Symbol path is outside the publish payload.'
    }
    Remove-Item -LiteralPath $_.FullName -Force
}

$sdkRoot = Join-Path $env:USERPROFILE '.nuget/packages/microsoft.windows.sdk.buildtools'
$sdk = Get-ChildItem $sdkRoot -Filter makeappx.exe -Recurse |
    Where-Object { $_.Directory.Name -eq 'x64' } |
    Sort-Object FullName -Descending | Select-Object -First 1
if (!$sdk) { throw 'Windows SDK MakeAppx.exe was not found.' }
$signTool = Join-Path $sdk.Directory.FullName 'signtool.exe'

[xml]$manifest = Get-Content (Join-Path $repoRoot 'packaging/AppxManifest.xml') -Raw
$manifest.Package.Identity.Version = $packageVersion
$manifest.Package.Identity.ProcessorArchitecture = $Architecture
$manifest.Save((Join-Path $payload 'AppxManifest.xml'))

# Reuse the application's existing icon for Windows package assets.
Add-Type -AssemblyName System.Drawing
$source = [System.Drawing.Image]::FromFile((Join-Path $repoRoot 'docs/LOGO.png'))
try {
    foreach ($asset in @(
        @{ Name = 'StoreLogo.png'; Size = 50 },
        @{ Name = 'Square44x44Logo.png'; Size = 44 },
        @{ Name = 'Square150x150Logo.png'; Size = 150 }
    )) {
        $bitmap = [System.Drawing.Bitmap]::new($asset.Size, $asset.Size)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $scale = [Math]::Min($asset.Size / $source.Width, $asset.Size / $source.Height)
            $width = [int][Math]::Round($source.Width * $scale)
            $height = [int][Math]::Round($source.Height * $scale)
            $graphics.DrawImage($source, [int](($asset.Size - $width) / 2), [int](($asset.Size - $height) / 2), $width, $height)
            $bitmap.Save((Join-Path $payload "Assets/$($asset.Name)"), [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
}
finally { $source.Dispose() }

$makePri = Join-Path $sdk.Directory.FullName 'makepri.exe'
$priInputs = Join-Path $outputRoot 'pri-inputs.resfiles'
Get-ChildItem $payload -Filter '*.pri' | Where-Object Name -ne 'resources.pri' |
    ForEach-Object FullName | Set-Content $priInputs
$priConfig = Join-Path $outputRoot 'priconfig.xml'
$escapedRoot = [Security.SecurityElement]::Escape($outputRoot)
@"
<?xml version="1.0" encoding="utf-8"?>
<resources targetOsVersion="10.0.0" majorVersion="1">
  <index root="$escapedRoot" startIndexAt="pri-inputs.resfiles">
    <default><qualifier name="Language" value="en-US" /></default>
    <indexer-config type="PRI" />
    <indexer-config type="RESFILES" qualifierDelimiter="." />
  </index>
</resources>
"@ | Set-Content $priConfig
# Packaged MRT lookup requires resources.pri with the package identity. Merely
# copying Reactor.pri and WinUI's standalone PRIs works only when unpackaged.
& $makePri new /pr $payload /cf $priConfig /mn (Join-Path $payload 'AppxManifest.xml') `
    /in $manifest.Package.Identity.Name /of (Join-Path $payload 'resources.pri') /o > (Join-Path $outputRoot 'makepri.log')
if ($LASTEXITCODE -ne 0) { throw 'Package resource index generation failed.' }
$priDump = Join-Path $outputRoot 'resources.pri.xml'
& $makePri dump /if (Join-Path $payload 'resources.pri') /of $priDump /o > (Join-Path $outputRoot 'makepri-dump.log')
if ($LASTEXITCODE -ne 0) { throw 'Package resource index validation failed.' }
if (!(Select-String -LiteralPath $priDump -Pattern 'ReactorApplication.xbf' -SimpleMatch -Quiet)) {
    throw 'Merged package resource index is missing Reactor startup resources.'
}

$package = Join-Path $outputRoot "SurgeLink_$($packageVersion)_$Architecture.appx"
& $sdk.FullName pack /d $payload /p $package /o > (Join-Path $outputRoot 'makeappx.log')
if ($LASTEXITCODE -ne 0) { throw 'APPX validation or packaging failed.' }

# Generate an ephemeral development signing key in memory. Do not add trust or
# install a certificate on the build machine. Only the public certificate remains.
$rsa = [System.Security.Cryptography.RSA]::Create(3072)
$certificate = $null
$temporaryPfx = Join-Path $outputRoot "signing-$([Guid]::NewGuid().ToString('N')).pfx"
try {
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
        $manifest.Package.Identity.Publisher, $rsa,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
    $request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
        [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
    $usages = [System.Security.Cryptography.OidCollection]::new()
    $null = $usages.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
    $request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usages, $true))
    $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddYears(1))
    [IO.File]::WriteAllBytes($temporaryPfx, $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx))
    [IO.File]::WriteAllBytes((Join-Path $outputRoot 'SurgeLink-Development.cer'), $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    & $signTool sign /fd SHA256 /f $temporaryPfx $package
    if ($LASTEXITCODE -ne 0) { throw 'APPX signing failed.' }
}
finally {
    if (Test-Path -LiteralPath $temporaryPfx) { Remove-Item -LiteralPath $temporaryPfx -Force }
    if ($certificate) { $certificate.Dispose() }
    $rsa.Dispose()
}

$hash = Get-FileHash -LiteralPath $package -Algorithm SHA256
"$($hash.Hash)  $([IO.Path]::GetFileName($package))" | Set-Content (Join-Path $outputRoot 'SHA256.txt')
Get-Item -LiteralPath $package | Select-Object FullName, Length

$distribution = Join-Path $outputRoot "SurgeLink_$($Version)_$Architecture"
$null = New-Item -ItemType Directory -Force -Path $distribution
$certificatePath = Join-Path $outputRoot 'SurgeLink-Development.cer'
$publicCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
try { $thumbprint = $publicCertificate.Thumbprint }
finally { $publicCertificate.Dispose() }
$installer = Get-Content (Join-Path $repoRoot 'packaging/Install.ps1.template') -Raw
$installer = $installer.Replace('__PACKAGE_NAME__', [IO.Path]::GetFileName($package)).Replace('__PACKAGE_HASH__', $hash.Hash).Replace('__CERT_THUMBPRINT__', $thumbprint)
$installerPath = Join-Path $distribution 'Install.ps1'
$installer | Set-Content -LiteralPath $installerPath -Encoding utf8BOM
$distributionPackage = Join-Path $distribution ([IO.Path]::GetFileName($package))
$distributionCertificate = Join-Path $distribution 'SurgeLink-Development.cer'
Copy-Item -LiteralPath $package -Destination $distributionPackage -Force
Copy-Item -LiteralPath $certificatePath -Destination $distributionCertificate -Force
$zipPath = Join-Path $outputRoot "SurgeLink_$($Version)_$Architecture.zip"
Compress-Archive -LiteralPath @($distributionPackage, $distributionCertificate, $installerPath) -DestinationPath $zipPath -Force
Get-Item -LiteralPath $zipPath | Select-Object FullName, Length
