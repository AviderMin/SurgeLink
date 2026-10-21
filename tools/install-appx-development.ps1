param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$outputRoot = Join-Path $repoRoot 'artifacts/appx'
$logPath = Join-Path $outputRoot 'install-0.1.0.1.log'
try {
    $certificatePath = Join-Path $outputRoot 'SurgeLink-Development.cer'
    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
    try {
        if ($certificate.Thumbprint -ne 'BCAB89391AB9B9BFD133B18E2B84A437E328149D') {
            throw 'Certificate differs from the exact development certificate authorized for this repair.'
        }
    }
    finally { $certificate.Dispose() }
    Import-Certificate -FilePath $certificatePath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
    Add-AppxPackage -Path (Join-Path $outputRoot 'SurgeLink_0.1.0.1_x64.appx')
    Get-AppxPackage -Name SurgeLink | Select-Object PackageFullName, InstallLocation, Status |
        ConvertTo-Json | Set-Content -LiteralPath $logPath
}
catch {
    $_ | Out-String | Set-Content -LiteralPath $logPath
    exit 1
}
