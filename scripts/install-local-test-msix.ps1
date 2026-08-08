[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $CertificatePath,

    [Parameter(Mandatory = $true)]
    [string] $PackagePath,

    [string] $PackageIdentityName,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string] $ExpectedThumbprint,

    [Parameter(Mandatory = $true)]
    [string] $ResultPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-InstallResult {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable] $Value
    )

    $resultDirectory = Split-Path -Parent $ResultPath
    if (-not [string]::IsNullOrWhiteSpace($resultDirectory)) {
        New-Item -ItemType Directory -Path $resultDirectory -Force |
            Out-Null
    }

    $utf8NoBom = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText(
        $ResultPath,
        ($Value | ConvertTo-Json -Depth 4),
        $utf8NoBom)
}

try {
    $principal = New-Object Security.Principal.WindowsPrincipal(
        [Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Administrator elevation is required."
    }

    $resolvedCertificatePath = [IO.Path]::GetFullPath($CertificatePath)
    $resolvedPackagePath = [IO.Path]::GetFullPath($PackagePath)
    $normalizedThumbprint = $ExpectedThumbprint.ToUpperInvariant()

    if (-not (Test-Path -LiteralPath $resolvedCertificatePath -PathType Leaf)) {
        throw "Certificate file was not found: $resolvedCertificatePath"
    }

    if (-not (Test-Path -LiteralPath $resolvedPackagePath -PathType Leaf)) {
        throw "MSIX package was not found: $resolvedPackagePath"
    }

    if ([string]::IsNullOrWhiteSpace($PackageIdentityName)) {
        $metadataPath = "$resolvedPackagePath.metadata.json"
        if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
            throw (
                "Package identity was not supplied and package metadata was " +
                "not found. Pass -PackageIdentityName or keep the generated " +
                ".metadata.json file next to the MSIX package.")
        }

        $metadata = Get-Content `
            -LiteralPath $metadataPath `
            -Raw `
            -Encoding utf8 |
            ConvertFrom-Json
        $PackageIdentityName = [string]$metadata.identityName
    }

    if ([string]::IsNullOrWhiteSpace($PackageIdentityName) -or
        $PackageIdentityName -notmatch '^[A-Za-z0-9.-]{3,50}$') {
        throw "Package identity name has an invalid MSIX format."
    }

    $certificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2(
        $resolvedCertificatePath)
    if ($certificate.Thumbprint.ToUpperInvariant() -ne $normalizedThumbprint) {
        throw "Certificate thumbprint does not match the expected value."
    }

    $packageSignature = Get-AuthenticodeSignature `
        -LiteralPath $resolvedPackagePath
    if ($null -eq $packageSignature.SignerCertificate -or
        $packageSignature.SignerCertificate.Thumbprint.ToUpperInvariant() -ne
            $normalizedThumbprint) {
        throw "MSIX signer does not match the expected certificate."
    }

    Import-Certificate `
        -FilePath $resolvedCertificatePath `
        -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" |
        Out-Null

    Add-AppxPackage `
        -Path $resolvedPackagePath `
        -ForceApplicationShutdown `
        -ErrorAction Stop

    $package = Get-AppxPackage -Name $PackageIdentityName |
        Sort-Object Version -Descending |
        Select-Object -First 1
    if ($null -eq $package) {
        throw "Package installation completed but the package was not found."
    }

    Write-InstallResult -Value @{
        passed = $true
        certificateThumbprint = $normalizedThumbprint
        certificateStore = "LocalMachine\TrustedPeople"
        packageFullName = $package.PackageFullName
        packageFamilyName = $package.PackageFamilyName
        packageVersion = $package.Version.ToString()
        publisher = $package.Publisher
        installLocation = $package.InstallLocation
    }
}
catch {
    Write-InstallResult -Value @{
        passed = $false
        error = $_.Exception.Message
        exceptionType = $_.Exception.GetType().FullName
    }
    exit 1
}
