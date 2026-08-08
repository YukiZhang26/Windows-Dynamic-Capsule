[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $CertificateThumbprint,

    [string] $ExpectedPublisher,

    [switch] $RequireTrustedChain
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "..\packaging\Packaging.Common.ps1")

$certificate = Get-CodeSigningCertificate `
    -Thumbprint $CertificateThumbprint

if ((-not [string]::IsNullOrWhiteSpace($ExpectedPublisher)) -and
    (-not [string]::Equals(
        $ExpectedPublisher,
        $certificate.Subject,
        [StringComparison]::Ordinal))) {
    throw (
        "Expected publisher does not exactly match the certificate subject. " +
        "Certificate subject: $($certificate.Subject)")
}

$chain = New-Object Security.Cryptography.X509Certificates.X509Chain
$chain.ChainPolicy.RevocationMode =
    [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
try {
    $chainTrusted = $chain.Build($certificate)
    $chainStatus = @(
        $chain.ChainStatus |
            ForEach-Object {
                [PSCustomObject]@{
                    Status = $_.Status.ToString()
                    Information = $_.StatusInformation.Trim()
                }
            }
    )
}
finally {
    $chain.Dispose()
}

if ($RequireTrustedChain -and -not $chainTrusted) {
    throw "The certificate does not chain to a trusted root on this machine."
}

[PSCustomObject]@{
    Subject = $certificate.Subject
    Issuer = $certificate.Issuer
    Thumbprint = $certificate.Thumbprint
    NotBefore = $certificate.NotBefore
    NotAfter = $certificate.NotAfter
    HasPrivateKey = $certificate.HasPrivateKey
    ChainTrusted = $chainTrusted
    ChainStatus = $chainStatus
    CertificateStoreModified = $false
    FilesSigned = 0
}
