param(
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.2',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$outputRoot = Join-Path $repoRoot 'artifacts/appx'
$payload = Join-Path $outputRoot 'payload'
$null = New-Item -ItemType Directory -Force -Path $outputRoot

if (!$SkipPublish) {
    & (Join-Path $PSScriptRoot 'make-icon.ps1')
    & dotnet publish (Join-Path $repoRoot 'src/SurgeLink.App/SurgeLink.App.csproj') `
        -c Release -r win-x64 --self-contained true -p:Platform=x64 `
        -p:WindowsAppSDKSelfContained=true -p:UseSharedCompilation=false `
        -p:PublishTrimmed=false -o $payload -m:1 -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
}
if (!(Test-Path (Join-Path $payload 'SurgeLink.exe'))) { throw 'Publish output is missing.' }

$sdkRoot = Join-Path $env:USERPROFILE '.nuget/packages/microsoft.windows.sdk.buildtools'
$sdk = Get-ChildItem $sdkRoot -Filter makeappx.exe -Recurse |
    Where-Object { $_.Directory.Name -eq 'x64' } |
    Sort-Object FullName -Descending | Select-Object -First 1
if (!$sdk) { throw 'Windows SDK MakeAppx.exe was not found.' }
$signTool = Join-Path $sdk.Directory.FullName 'signtool.exe'

[xml]$manifest = Get-Content (Join-Path $repoRoot 'packaging/AppxManifest.xml') -Raw
$manifest.Package.Identity.Version = $Version
$manifest.Save((Join-Path $payload 'AppxManifest.xml'))

# Reuse the application's existing icon for Windows package assets.
Add-Type -AssemblyName System.Drawing
$source = [System.Drawing.Image]::FromFile((Join-Path $repoRoot 'docs/ICON.jpg'))
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
            $graphics.DrawImage($source, 0, 0, $asset.Size, $asset.Size)
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

$package = Join-Path $outputRoot "SurgeLink_$($Version)_x64.appx"
& $sdk.FullName pack /d $payload /p $package /o > (Join-Path $outputRoot 'makeappx.log')
if ($LASTEXITCODE -ne 0) { throw 'APPX validation or packaging failed.' }

# Generate an ephemeral development signing key in memory. Do not add trust or
# install a certificate on the build machine. Only the public certificate remains.
$rsa = [System.Security.Cryptography.RSA]::Create(3072)
$certificate = $null
$temporaryPfx = Join-Path $outputRoot "signing-$([Guid]::NewGuid().ToString('N')).pfx"
try {
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=SurgeLink Development', $rsa,
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
