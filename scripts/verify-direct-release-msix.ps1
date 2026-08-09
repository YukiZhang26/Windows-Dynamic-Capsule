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
$temporaryExecutable = Join-Path `
    ([IO.Path]::GetTempPath()) `
    ("WindowsDynamicCapsule-release-{0}.exe" -f [Guid]::NewGuid().ToString("N"))
$archive = [IO.Compression.ZipFile]::OpenRead($resolvedPackagePath)
try {
    $executableEntry = @(
        $archive.Entries |
            Where-Object {
                $_.FullName -ieq "WindowsDynamicCapsule.exe"
            }
    ) | Select-Object -First 1
    if ($null -eq $executableEntry) {
        throw "WindowsDynamicCapsule.exe was not found in the MSIX."
    }

    [IO.Compression.ZipFileExtensions]::ExtractToFile(
        $executableEntry,
        $temporaryExecutable,
        $true)
}
finally {
    $archive.Dispose()
}

try {
    $executableSignature = Get-AuthenticodeSignature `
        -LiteralPath $temporaryExecutable
    if ($executableSignature.Status -ne "Valid" -or
        $null -eq $executableSignature.SignerCertificate) {
        throw (
            "WindowsDynamicCapsule.exe inside the MSIX is not signed by " +
            "a trusted certificate.")
    }

    if ($executableSignature.SignerCertificate.Subject -cne
        $ExpectedPublisher) {
        throw (
            "The executable signer does not match the direct-release " +
            "Publisher. Expected '$ExpectedPublisher', got " +
            "'$($executableSignature.SignerCertificate.Subject)'.")
    }

    if ($null -eq $executableSignature.TimeStamperCertificate) {
        throw (
            "WindowsDynamicCapsule.exe inside the MSIX must have a " +
            "trusted timestamp.")
    }
    $executableTimestampChainRoot = Get-TrustedChainRoot `
        -Certificate $executableSignature.TimeStamperCertificate `
        -Label "Inner executable timestamp"
}
finally {
    if (Test-Path -LiteralPath $temporaryExecutable -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryExecutable -Force
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
    InnerExecutableTimestampRoot = $executableTimestampChainRoot
}
