[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath,

    [Parameter(Mandatory = $true)]
    [string] $ExpectedIdentityName,

    [Parameter(Mandatory = $true)]
    [string] $ExpectedPublisher,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d{1,5}(\.\d{1,5}){3}$')]
    [string] $ExpectedVersion,

    [ValidatePattern('^v\d+\.\d+\.\d+$')]
    [string] $ReleaseTag,

    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string[]] $RejectedCertificateThumbprint = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-TrustedChainRoot {
    param(
        [Parameter(Mandatory = $true)]
        [Security.Cryptography.X509Certificates.X509Certificate2]
        $Certificate,

        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
    $rootSubject = $null
    try {
        $chain.ChainPolicy.RevocationMode =
            [Security.Cryptography.X509Certificates.X509RevocationMode]::Online
        $chain.ChainPolicy.RevocationFlag =
            [Security.Cryptography.X509Certificates.X509RevocationFlag]::EntireChain
        $chain.ChainPolicy.VerificationFlags =
            [Security.Cryptography.X509Certificates.X509VerificationFlags]::NoFlag
        $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(20)

        if (-not $chain.Build($Certificate)) {
            $chainErrors = @(
                $chain.ChainStatus |
                    ForEach-Object {
                        "{0}: {1}" -f `
                            $_.Status, `
                            $_.StatusInformation.Trim()
                    }
            ) -join "; "
            throw "$Label certificate chain is not trusted: $chainErrors"
        }

        if ($chain.ChainElements.Count -lt 2) {
            throw (
                "$Label certificate does not chain to a separate " +
                "trusted root.")
        }

        $rootSubject = $chain.ChainElements[
            $chain.ChainElements.Count - 1].Certificate.Subject
    }
    finally {
        $chain.Dispose()
    }

    return $rootSubject
}

function Assert-InnerProjectBinary {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FilePath,

        [Parameter(Mandatory = $true)]
        [string] $DisplayName,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedPublisher,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedProductVersion,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedFileVersion
    )

    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($FilePath)
    if ($versionInfo.ProductName -cne "Windows Dynamic Capsule" -or
        $versionInfo.ProductVersion -cne $ExpectedProductVersion -or
        $versionInfo.FileVersion -cne $ExpectedFileVersion -or
        $versionInfo.CompanyName -cne "Yuki Zhang") {
        throw (
            "$DisplayName metadata does not match the release contract. " +
            "ProductName='$($versionInfo.ProductName)', " +
            "ProductVersion='$($versionInfo.ProductVersion)', " +
            "FileVersion='$($versionInfo.FileVersion)', " +
            "CompanyName='$($versionInfo.CompanyName)'.")
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $FilePath
    if ($signature.Status -ne "Valid" -or
        $null -eq $signature.SignerCertificate) {
        throw "$DisplayName inside the MSIX is not signed by a trusted certificate."
    }

    if ($signature.SignerCertificate.Subject -cne $ExpectedPublisher) {
        throw (
            "$DisplayName signer does not match the direct-release " +
            "Publisher. Expected '$ExpectedPublisher', got " +
            "'$($signature.SignerCertificate.Subject)'.")
    }

    if ($null -eq $signature.TimeStamperCertificate) {
        throw "$DisplayName inside the MSIX must have a trusted timestamp."
    }

    $timestampChainRoot = Get-TrustedChainRoot `
        -Certificate $signature.TimeStamperCertificate `
        -Label "$DisplayName timestamp"

    return [PSCustomObject]@{
        DisplayName = $DisplayName
        SignerSubject = $signature.SignerCertificate.Subject
        TimestampChainRoot = $timestampChainRoot
    }
}

$resolvedPackagePath = [IO.Path]::GetFullPath($PackagePath)
if (-not (Test-Path -LiteralPath $resolvedPackagePath -PathType Leaf)) {
    throw "MSIX package was not found: $resolvedPackagePath"
}

$packageFileName = [IO.Path]::GetFileName($resolvedPackagePath)
if ($packageFileName -match '(?i)(unsigned|local[._-]?test|self[._-]?signed)') {
    throw (
        "Public release package names must not contain development " +
        "markers such as unsigned, local-test, or self-signed: " +
        $packageFileName)
}

if (-not [string]::IsNullOrWhiteSpace($ReleaseTag)) {
    $null = $ReleaseTag -match '^v(\d+)\.(\d+)\.(\d+)$'
    $tagVersion = "{0}.{1}.{2}.0" -f `
        $Matches[1], $Matches[2], $Matches[3]
    if ($ExpectedVersion -ne $tagVersion) {
        throw (
            "Release tag $ReleaseTag maps to MSIX version $tagVersion, " +
            "not $ExpectedVersion.")
    }
}

$verificationScript = Join-Path $PSScriptRoot "verify-msix.ps1"
$verification = & $verificationScript `
    -PackagePath $resolvedPackagePath `
    -RequireSignature

if ($verification.IdentityName -cne $ExpectedIdentityName) {
    throw (
        "MSIX identity mismatch. Expected '$ExpectedIdentityName', got " +
        "'$($verification.IdentityName)'.")
}

if ($verification.Publisher -cne $ExpectedPublisher) {
    throw (
        "MSIX publisher mismatch. Expected '$ExpectedPublisher', got " +
        "'$($verification.Publisher)'.")
}

if ($verification.Version -cne $ExpectedVersion) {
    throw (
        "MSIX version mismatch. Expected '$ExpectedVersion', got " +
        "'$($verification.Version)'.")
}

$signature = Get-AuthenticodeSignature -LiteralPath $resolvedPackagePath
$signer = $signature.SignerCertificate
if ($null -eq $signer -or $signature.Status -ne "Valid") {
    throw "The public release MSIX does not have a valid signature."
}

if ($signer.Subject -cne $ExpectedPublisher) {
    throw (
        "The direct-release manifest Publisher must exactly match the " +
        "signing certificate subject. Manifest: '$ExpectedPublisher'; " +
        "certificate: '$($signer.Subject)'.")
}

$normalizedRejectedThumbprints = @(
    $RejectedCertificateThumbprint |
        ForEach-Object { ($_ -replace '\s', '').ToUpperInvariant() }
)
$signerThumbprint = $signer.Thumbprint.ToUpperInvariant()
if ($normalizedRejectedThumbprints -contains $signerThumbprint) {
    throw (
        "The package was signed by an explicitly rejected development " +
        "certificate: $signerThumbprint")
}

if ($signer.Subject -ceq $signer.Issuer) {
    throw (
        "Self-signed certificates are not permitted for public direct " +
        "downloads: $($signer.Subject)")
}

$chainRoot = Get-TrustedChainRoot `
    -Certificate $signer `
    -Label "Signing"

if ($null -eq $signature.TimeStamperCertificate) {
    throw "The public release MSIX must have a trusted timestamp."
}
$timestampCertificate = $signature.TimeStamperCertificate
$timestampChainRoot = Get-TrustedChainRoot `
    -Certificate $timestampCertificate `
    -Label "Timestamp"

Add-Type -AssemblyName System.IO.Compression.FileSystem
$temporaryFiles = [ordered]@{
    "WindowsDynamicCapsule.exe" = Join-Path `
        ([IO.Path]::GetTempPath()) `
        ("WindowsDynamicCapsule-release-{0}.exe" -f
            [Guid]::NewGuid().ToString("N"))
    "WindowsDynamicCapsule.dll" = Join-Path `
        ([IO.Path]::GetTempPath()) `
        ("WindowsDynamicCapsule-release-{0}.dll" -f
            [Guid]::NewGuid().ToString("N"))
}
try {
    $archive = [IO.Compression.ZipFile]::OpenRead($resolvedPackagePath)
    try {
        foreach ($entryName in $temporaryFiles.Keys) {
            $matchingEntries = @(
                $archive.Entries |
                    Where-Object { $_.FullName -ieq $entryName }
            )
            if ($matchingEntries.Count -ne 1) {
                throw (
                    "Expected exactly one $entryName inside the MSIX, found " +
                    "$($matchingEntries.Count).")
            }

            [IO.Compression.ZipFileExtensions]::ExtractToFile(
                $matchingEntries[0],
                $temporaryFiles[$entryName],
                $true)
        }
    }
    finally {
        $archive.Dispose()
    }

    $expectedProductVersion = (
        [version]$ExpectedVersion).ToString(3)
    $executableVerification = Assert-InnerProjectBinary `
        -FilePath $temporaryFiles["WindowsDynamicCapsule.exe"] `
        -DisplayName "WindowsDynamicCapsule.exe" `
        -ExpectedPublisher $ExpectedPublisher `
        -ExpectedProductVersion $expectedProductVersion `
        -ExpectedFileVersion $ExpectedVersion
    $libraryVerification = Assert-InnerProjectBinary `
        -FilePath $temporaryFiles["WindowsDynamicCapsule.dll"] `
        -DisplayName "WindowsDynamicCapsule.dll" `
        -ExpectedPublisher $ExpectedPublisher `
        -ExpectedProductVersion $expectedProductVersion `
        -ExpectedFileVersion $ExpectedVersion
}
finally {
    foreach ($temporaryFile in $temporaryFiles.Values) {
        if (Test-Path -LiteralPath $temporaryFile -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryFile -Force
        }
    }
}

[PSCustomObject]@{
    Passed = $true
    PublicReleaseEligible = $true
    Package = $resolvedPackagePath
    Sha256 = $verification.Sha256
    IdentityName = $verification.IdentityName
    Publisher = $verification.Publisher
    Version = $verification.Version
    SignerSubject = $signer.Subject
    SignerThumbprint = $signerThumbprint
    ChainRoot = $chainRoot
    TimestampSubject = $timestampCertificate.Subject
    TimestampChainRoot = $timestampChainRoot
    InnerExecutableSigned = $true
    InnerExecutableTimestampRoot =
        $executableVerification.TimestampChainRoot
    InnerLibrarySigned = $true
    InnerLibraryTimestampRoot =
        $libraryVerification.TimestampChainRoot
}
