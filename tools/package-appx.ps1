param(
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version = '0.1.0.0',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$outputRoot = Join-Path $repoRoot 'artifacts/appx'
$payload = Join-Path $outputRoot 'payload'
$null = New-Item -ItemType Directory -Force -Path $outputRoot

if (!$SkipPublish) {
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
$icon = [System.Drawing.Icon]::new((Join-Path $repoRoot 'src/SurgeLink.App/Assets/AppIcon.ico'), 256, 256)
$source = $icon.ToBitmap()
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
finally { $source.Dispose(); $icon.Dispose() }

$package = Join-Path $outputRoot "SurgeLink_$($Version)_x64.appx"
& $sdk.FullName pack /d $payload /p $package /o
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
