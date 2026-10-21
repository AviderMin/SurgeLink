param(
    [Parameter(Mandatory)][string]$ZipPath,
    [string]$ExpectedVersion = '1.0.2.0',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../../artifacts/blackbox/package')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Report.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Security
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$work = Join-Path $OutputDirectory ('work-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Force -Path $work
$script:zip = $null
$script:appx = $null
$script:packagePath = $null
$script:certificate = $null
$script:manifest = $null
$script:cms = $null
$script:installerValidated = $false
function Need-Package { if (!$script:appx) { throw 'SKIP: package could not be opened.' } }
function Read-Entry([string]$Name) {
    Need-Package
    $entry = $script:appx.GetEntry($Name)
    Assert-BB ($null -ne $entry) "Missing $Name"
    $reader = [IO.StreamReader]::new($entry.Open())
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}
function Invoke-InstallerValidation {
    $output = & powershell.exe -NoProfile -File (Join-Path $work 'Install.ps1') -ValidateOnly 2>&1 | Out-String
    return @{ Code = $LASTEXITCODE; Output = $output }
}
try {
    Invoke-BBCase 'PKG001' 'ZIP opens and contains exactly APPX, CER and installer' {
        $script:zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ZipPath))
        $names = @($script:zip.Entries.FullName)
        Assert-BB ($names.Count -eq 3) 'Expected exactly three entries.'
        Assert-BB (@($names | Where-Object { $_ -match '^SurgeLink_[\d.]+_x64\.appx$' }).Count -eq 1) 'Expected one x64 APPX.'
        Assert-BB ($names -contains 'SurgeLink-Development.cer' -and $names -contains 'Install.ps1') 'CER or Install.ps1 missing.'
        # Extract only a strict allowlist; no untrusted path traversal.
        foreach ($entry in $script:zip.Entries) {
            Assert-BB ($entry.FullName -notmatch '[/\\:]|\.\.') 'Unsafe ZIP entry.'
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $work $entry.FullName), $false)
        }
        $script:packagePath = (Get-ChildItem $work -Filter '*.appx').FullName
    }
    Invoke-BBCase 'PKG002' 'Distribution contains no private signing key' {
        if (!$script:zip) { throw 'SKIP: ZIP unavailable.' }
        Assert-BB (@($script:zip.Entries | Where-Object FullName -Match '\.(pfx|p12|key|pem)$').Count -eq 0) 'Private key in distribution.'
    }
    Invoke-BBCase 'PKG003' 'APPX opens with required package metadata' {
        if (!$script:packagePath) { throw 'SKIP: ZIP extraction failed.' }
        $script:appx = [IO.Compression.ZipFile]::OpenRead($script:packagePath)
        foreach ($name in @('AppxManifest.xml','AppxBlockMap.xml','AppxSignature.p7x','[Content_Types].xml')) {
            Assert-BB ($null -ne $script:appx.GetEntry($name)) "Missing $name"
        }
        $script:manifest = [xml](Read-Entry 'AppxManifest.xml')
    }
    Invoke-BBCase 'PKG004' 'Package version matches requested release' {
        if (!$script:manifest) { throw 'SKIP: manifest unavailable.' }
        Assert-BB ($script:manifest.Package.Identity.Version -eq $ExpectedVersion) 'Incorrect release version.'
    }
    Invoke-BBCase 'PKG005' 'Package targets x64 Windows Desktop' {
        if (!$script:manifest) { throw 'SKIP: manifest unavailable.' }
        Assert-BB ($script:manifest.Package.Identity.ProcessorArchitecture -eq 'x64') 'Incorrect architecture.'
        Assert-BB ($script:manifest.Package.Dependencies.TargetDeviceFamily.Name -eq 'Windows.Desktop') 'Incorrect device family.'
    }
    Invoke-BBCase 'PKG006' 'Launch entry resolves to shipped executable' {
        if (!$script:manifest) { throw 'SKIP: manifest unavailable.' }
        $application = $script:manifest.Package.Applications.Application
        Assert-BB ($application.EntryPoint -eq 'Windows.FullTrustApplication') 'Incorrect desktop entry point.'
        Assert-BB ($null -ne $script:appx.GetEntry($application.Executable)) 'Manifest executable missing.'
    }
    Invoke-BBCase 'PKG007' 'Merged startup resource index exists and is nonempty' {
        Need-Package
        $entry = $script:appx.GetEntry('resources.pri')
        Assert-BB ($null -ne $entry -and $entry.Length -gt 1024) 'Missing or empty merged resources.pri; installed startup must also be tested.'
    }
    Invoke-BBCase 'PKG008' 'Self-contained .NET and WinUI runtime present' {
        Need-Package
        foreach ($name in @('coreclr.dll','hostfxr.dll','hostpolicy.dll','Microsoft.ui.xaml.dll','SurgeLink.runtimeconfig.json')) {
            Assert-BB (@($script:appx.Entries | Where-Object FullName -EQ $name).Count -eq 1) "Missing runtime file $name"
        }
        $runtime = (Read-Entry 'SurgeLink.runtimeconfig.json') | ConvertFrom-Json
        Assert-BB ($null -ne $runtime.runtimeOptions.includedFrameworks) '.NET runtime is framework-dependent.'
    }
    Invoke-BBCase 'PKG009' 'Manifest icons exist in package' {
        Need-Package
        if (!$script:manifest) { throw 'SKIP: manifest unavailable.' }
        $visuals = $script:manifest.Package.Applications.Application.VisualElements
        foreach ($name in @($script:manifest.Package.Properties.Logo, $visuals.Square44x44Logo, $visuals.Square150x150Logo)) {
            $entry = $script:appx.GetEntry($name.Replace('\','/'))
            Assert-BB ($null -ne $entry -and $entry.Length -gt 0) "Missing icon $name"
        }
    }
    Invoke-BBCase 'PKG010' 'Development certificate is readable and currently valid' {
        $script:certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $work 'SurgeLink-Development.cer'))
        Assert-BB ((Get-Date) -ge $script:certificate.NotBefore -and (Get-Date) -lt $script:certificate.NotAfter) 'Certificate outside validity interval.'
    }
    Invoke-BBCase 'PKG011' 'Certificate grants code signing usage' {
        if (!$script:certificate) { throw 'SKIP: certificate unavailable.' }
        $usage = @($script:certificate.Extensions | Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] })
        Assert-BB (@($usage.EnhancedKeyUsages | Where-Object Value -EQ '1.3.6.1.5.5.7.3.3').Count -gt 0) 'Code signing EKU missing.'
    }
    Invoke-BBCase 'PKG012' 'APPX CMS cryptographic signature is valid' {
        Need-Package
        $stream = $script:appx.GetEntry('AppxSignature.p7x').Open()
        $memory = [IO.MemoryStream]::new()
        try { $stream.CopyTo($memory); $bytes = $memory.ToArray() }
        finally { $stream.Dispose(); $memory.Dispose() }
        Assert-BB ([Text.Encoding]::ASCII.GetString($bytes,0,4) -eq 'PKCX') 'Invalid APPX signature header.'
        $script:cms = [Security.Cryptography.Pkcs.SignedCms]::new()
        $script:cms.Decode($bytes[4..($bytes.Length-1)])
        $script:cms.CheckSignature($true)
    }
    Invoke-BBCase 'PKG013' 'Signing certificate matches CER and manifest publisher' {
        if (!$script:cms -or !$script:certificate -or !$script:manifest) { throw 'SKIP: signature/certificate/manifest unavailable.' }
        Assert-BB ($script:cms.SignerInfos[0].Certificate.Thumbprint -eq $script:certificate.Thumbprint) 'Bundled CER differs from signer.'
        Assert-BB ($script:certificate.Subject -eq $script:manifest.Package.Identity.Publisher) 'Publisher differs from certificate.'
    }
    Invoke-BBCase 'PKG014' 'Installer PowerShell syntax is valid' {
        $tokens = $null; $errors = $null
        $null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $work 'Install.ps1'),[ref]$tokens,[ref]$errors)
        Assert-BB ($errors.Count -eq 0) ($errors | Out-String)
    }
    Invoke-BBCase 'PKG015' 'Installer validates authentic files without installation' {
        if (!$script:packagePath -or !$script:certificate -or !$script:cms) { throw 'SKIP: authentic package validation unavailable.' }
        $result = Invoke-InstallerValidation
        Assert-BB ($result.Code -eq 0 -and $result.Output -match 'No changes made') $result.Output
        $script:installerValidated = $true
    }
    Invoke-BBCase 'PKG016' 'Installer rejects tampered APPX' {
        if (!$script:installerValidated) { throw 'SKIP: baseline installer validation failed.' }
        $script:appx.Dispose()
        $script:appx = $null
        $stream = [IO.File]::Open($script:packagePath, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        try { $stream.WriteByte(1) } finally { $stream.Dispose() }
        try {
            $result = Invoke-InstallerValidation
            $normalized = $result.Output -replace '\s', ''
            Assert-BB ($result.Code -ne 0 -and $normalized -match 'APPXchecksummismatch') "Tampered package not rejected for checksum mismatch. Exit=$($result.Code); $($result.Output)"
        }
        finally {
            $stream = [IO.File]::OpenWrite($script:packagePath)
            try { $stream.SetLength($stream.Length - 1) } finally { $stream.Dispose() }
        }
    }
    Invoke-BBCase 'PKG017' 'Installer rejects missing certificate' {
        if (!$script:installerValidated) { throw 'SKIP: baseline installer validation failed.' }
        $path = Join-Path $work 'SurgeLink-Development.cer'
        $backup = Join-Path $work 'certificate.backup'
        Move-Item -LiteralPath $path -Destination $backup
        try { $result = Invoke-InstallerValidation; Assert-BB ($result.Code -ne 0) 'Missing certificate accepted.' }
        finally { Move-Item -LiteralPath $backup -Destination $path }
    }
    Invoke-BBCase 'PKG018' 'Installer rejects missing APPX' {
        if (!$script:installerValidated) { throw 'SKIP: baseline installer validation failed.' }
        $backup = Join-Path $work 'package.backup'
        Move-Item -LiteralPath $script:packagePath -Destination $backup
        try { $result = Invoke-InstallerValidation; Assert-BB ($result.Code -ne 0) 'Missing APPX accepted.' }
        finally { Move-Item -LiteralPath $backup -Destination $script:packagePath }
    }
}
finally {
    if ($script:appx) { $script:appx.Dispose() }
    if ($script:zip) { $script:zip.Dispose() }
    if ($script:certificate) { $script:certificate.Dispose() }
}
$ok = Export-BBReport -OutputDirectory $OutputDirectory -Suite 'Package' -Context @{Zip=$ZipPath;Version=$ExpectedVersion;WorkDirectory=$work}
if (!$ok) { exit 1 }
