[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $CertificatePath,

    [Parameter(Mandatory = $true)]
    [string] $PackagePath,

    [string] $MetadataPath,

    [string] $PackageIdentityName,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string] $ExpectedThumbprint,

    [Parameter(Mandatory = $true)]
    [string] $ResultPath,

    [string] $SettingsPath = (
        Join-Path $env:LOCALAPPDATA "WindowsDynamicCapsule\settings.json"),

    [switch] $PreflightOnly,

    [switch] $AllowSameVersionReinstall
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

$certificateWasImported = $false
$packageWasApplied = $false
$trustedCertificatePath = $null

try {
    $resolvedCertificatePath = [IO.Path]::GetFullPath($CertificatePath)
    $resolvedPackagePath = [IO.Path]::GetFullPath($PackagePath)
    $resolvedMetadataPath = if ([string]::IsNullOrWhiteSpace(
            $MetadataPath)) {
        "$resolvedPackagePath.metadata.json"
    }
    else {
        [IO.Path]::GetFullPath($MetadataPath)
    }
    $resolvedSettingsPath = [IO.Path]::GetFullPath($SettingsPath)
    $normalizedThumbprint = $ExpectedThumbprint.ToUpperInvariant()

    if (-not (Test-Path -LiteralPath $resolvedCertificatePath -PathType Leaf)) {
        throw "Certificate file was not found: $resolvedCertificatePath"
    }

    if (-not (Test-Path -LiteralPath $resolvedPackagePath -PathType Leaf)) {
        throw "MSIX package was not found: $resolvedPackagePath"
    }

    if (-not (Test-Path -LiteralPath $resolvedMetadataPath -PathType Leaf)) {
        throw (
            "Generated package metadata was not found: " +
            $resolvedMetadataPath)
    }

    $metadata = Get-Content `
        -LiteralPath $resolvedMetadataPath `
        -Raw `
        -Encoding utf8 |
        ConvertFrom-Json
    $requiredMetadataProperties = @(
        "schemaVersion"
        "sha256"
        "identityName"
        "publisher"
        "packageVersion"
        "architecture"
        "signed"
        "certificateThumbprint"
        "timestamped")
    $metadataPropertyNames = @($metadata.PSObject.Properties.Name)
    $missingMetadataProperties = @(
        $requiredMetadataProperties |
            Where-Object { $_ -notin $metadataPropertyNames })
    if ($missingMetadataProperties.Count -gt 0) {
        throw (
            "Package metadata is missing: " +
            ($missingMetadataProperties -join ", "))
    }

    if ($metadata.schemaVersion -notin @(1, 2) -or
        $metadata.signed -ne $true -or
        $metadata.timestamped -ne $true) {
        throw "Package metadata does not describe a signed local-test build."
    }
    if ($metadata.schemaVersion -eq 2 -and
        ($metadata.PSObject.Properties.Name -notcontains "sourceCommit" -or
         $metadata.PSObject.Properties.Name -notcontains "sourceDirty" -or
         [string]::IsNullOrWhiteSpace([string]$metadata.sourceCommit))) {
        throw "Package metadata does not contain complete source provenance."
    }

    $metadataThumbprint = (
        [string]$metadata.certificateThumbprint).ToUpperInvariant()
    if ($metadataThumbprint -ne $normalizedThumbprint) {
        throw "Package metadata signer does not match the expected certificate."
    }

    $packageHash = Get-FileHash `
        -LiteralPath $resolvedPackagePath `
        -Algorithm SHA256
    if ($packageHash.Hash -ne ([string]$metadata.sha256).ToUpperInvariant()) {
        throw "MSIX SHA-256 does not match the generated metadata."
    }

    if ([string]::IsNullOrWhiteSpace($PackageIdentityName)) {
        $PackageIdentityName = [string]$metadata.identityName
    }
    elseif ($PackageIdentityName -cne [string]$metadata.identityName) {
        throw "PackageIdentityName does not match the generated metadata."
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
    if ($certificate.Subject -cne [string]$metadata.publisher) {
        throw "Certificate subject does not match the package Publisher."
    }

    $packageSignature = Get-AuthenticodeSignature `
        -LiteralPath $resolvedPackagePath
    if ($null -eq $packageSignature.SignerCertificate -or
        $packageSignature.SignerCertificate.Thumbprint.ToUpperInvariant() -ne
            $normalizedThumbprint) {
        throw "MSIX signer does not match the expected certificate."
    }
    if ($null -eq $packageSignature.TimeStamperCertificate) {
        throw "MSIX does not contain the required trusted timestamp."
    }

    $verifyScript = Join-Path $PSScriptRoot "verify-msix.ps1"
    $packageVerification = & $verifyScript `
        -PackagePath $resolvedPackagePath
    if (-not $packageVerification.HasPackageSignature -or
        $packageVerification.IdentityName -cne
            [string]$metadata.identityName -or
        $packageVerification.Publisher -cne
            [string]$metadata.publisher -or
        $packageVerification.Version -cne
            [string]$metadata.packageVersion -or
        $packageVerification.Architecture -cne
            [string]$metadata.architecture) {
        throw "MSIX contents do not match the generated metadata."
    }

    $candidateVersion = [version]$packageVerification.Version
    $previousPackage = Get-AppxPackage -Name $PackageIdentityName |
        Sort-Object Version -Descending |
        Select-Object -First 1
    $previousVersion = if ($null -eq $previousPackage) {
        $null
    }
    else {
        [version]$previousPackage.Version
    }
    if ($null -ne $previousVersion -and
        $candidateVersion -lt $previousVersion) {
        throw (
            "Package downgrade is not allowed. Installed: " +
            "$previousVersion; candidate: $candidateVersion.")
    }
    if ($null -ne $previousVersion -and
        $candidateVersion -eq $previousVersion -and
        -not $AllowSameVersionReinstall) {
        throw (
            "The same package version is already installed. Use " +
            "-AllowSameVersionReinstall only for an intentional repair.")
    }

    $operation = if ($null -eq $previousVersion) {
        "Install"
    }
    elseif ($candidateVersion -eq $previousVersion) {
        "Repair"
    }
    else {
        "Upgrade"
    }
    $settingsExistedBefore = Test-Path `
        -LiteralPath $resolvedSettingsPath `
        -PathType Leaf
    $settingsHashBefore = if ($settingsExistedBefore) {
        (Get-FileHash `
            -LiteralPath $resolvedSettingsPath `
            -Algorithm SHA256).Hash
    }
    else {
        $null
    }

    if ($PreflightOnly) {
        Write-InstallResult -Value @{
            passed = $true
            preflightOnly = $true
            operation = $operation
            certificateThumbprint = $normalizedThumbprint
            previousPackageFullName = if ($null -eq $previousPackage) {
                $null
            } else {
                $previousPackage.PackageFullName
            }
            previousPackageVersion = if ($null -eq $previousVersion) {
                $null
            } else {
                $previousVersion.ToString()
            }
            candidateIdentityName = $packageVerification.IdentityName
            candidateVersion = $candidateVersion.ToString()
            candidatePublisher = $packageVerification.Publisher
            packageSha256 = $packageHash.Hash
            settingsPath = $resolvedSettingsPath
            settingsExisted = $settingsExistedBefore
            settingsSha256 = $settingsHashBefore
            trustedSignatureVerificationPending = $true
            systemStateModified = $false
        }
        return
    }

    $principal = New-Object Security.Principal.WindowsPrincipal(
        [Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Administrator elevation is required after preflight."
    }

    $trustedCertificatePath = (
        "Cert:\LocalMachine\TrustedPeople\$normalizedThumbprint")
    if (-not (Test-Path -LiteralPath $trustedCertificatePath)) {
        Import-Certificate `
            -FilePath $resolvedCertificatePath `
            -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" |
            Out-Null
        $certificateWasImported = $true
    }

    $trustedVerification = & $verifyScript `
        -PackagePath $resolvedPackagePath `
        -RequireSignature
    if ($trustedVerification.SignerSubject -cne
        [string]$metadata.publisher) {
        throw "Trusted MSIX signer does not match the package Publisher."
    }

    Add-AppxPackage `
        -Path $resolvedPackagePath `
        -ForceApplicationShutdown `
        -ErrorAction Stop
    $packageWasApplied = $true

    $package = Get-AppxPackage -Name $PackageIdentityName |
        Sort-Object Version -Descending |
        Select-Object -First 1
    if ($null -eq $package) {
        throw "Package installation completed but the package was not found."
    }
    if ([version]$package.Version -ne $candidateVersion -or
        $package.Publisher -cne [string]$metadata.publisher) {
        throw "Installed package identity does not match the candidate MSIX."
    }

    $settingsExistsAfter = Test-Path `
        -LiteralPath $resolvedSettingsPath `
        -PathType Leaf
    $settingsHashAfter = if ($settingsExistsAfter) {
        (Get-FileHash `
            -LiteralPath $resolvedSettingsPath `
            -Algorithm SHA256).Hash
    }
    else {
        $null
    }
    $settingsPreserved =
        $settingsExistedBefore -eq $settingsExistsAfter -and
        $settingsHashBefore -eq $settingsHashAfter
    if (-not $settingsPreserved) {
        throw "The local settings file changed during package installation."
    }

    Write-InstallResult -Value @{
        passed = $true
        operation = $operation
        certificateThumbprint = $normalizedThumbprint
        certificateStore = "LocalMachine\TrustedPeople"
        certificateImported = $certificateWasImported
        previousPackageFullName = if ($null -eq $previousPackage) {
            $null
        } else {
            $previousPackage.PackageFullName
        }
        previousPackageVersion = if ($null -eq $previousVersion) {
            $null
        } else {
            $previousVersion.ToString()
        }
        packageFullName = $package.PackageFullName
        packageFamilyName = $package.PackageFamilyName
        packageVersion = $package.Version.ToString()
        publisher = $package.Publisher
        installLocation = $package.InstallLocation
        packageSha256 = $packageHash.Hash
        settingsPath = $resolvedSettingsPath
        settingsExisted = $settingsExistsAfter
        settingsSha256Before = $settingsHashBefore
        settingsSha256After = $settingsHashAfter
        settingsPreserved = $settingsPreserved
    }
}
catch {
    $installError = $_.Exception.Message
    $installExceptionType = $_.Exception.GetType().FullName
    $certificateRollbackError = $null
    if ($certificateWasImported -and
        -not $packageWasApplied -and
        -not [string]::IsNullOrWhiteSpace($trustedCertificatePath) -and
        (Test-Path -LiteralPath $trustedCertificatePath)) {
        try {
            Remove-Item -LiteralPath $trustedCertificatePath -Force
        }
        catch {
            $certificateRollbackError = $_.Exception.Message
        }
    }

    Write-InstallResult -Value @{
        passed = $false
        error = $installError
        exceptionType = $installExceptionType
        certificateRolledBack =
            $certificateWasImported -and
            -not $packageWasApplied -and
            $null -eq $certificateRollbackError
        certificateRollbackError = $certificateRollbackError
    }
    exit 1
}
