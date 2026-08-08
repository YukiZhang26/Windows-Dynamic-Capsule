[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath,

    [switch] $RequireSignature
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resolvedPackagePath = [IO.Path]::GetFullPath($PackagePath)
if (-not (Test-Path -LiteralPath $resolvedPackagePath -PathType Leaf)) {
    throw "MSIX package was not found: $resolvedPackagePath"
}

if ([IO.Path]::GetExtension($resolvedPackagePath) -ne ".msix") {
    throw "PackagePath must point to an .msix file."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($resolvedPackagePath)
try {
    $entries = @{}
    foreach ($entry in $archive.Entries) {
        $entries[$entry.FullName.ToLowerInvariant()] = $entry
    }

    $requiredEntries = @(
        "appxmanifest.xml",
        "appxblockmap.xml",
        "[content_types].xml",
        "windowsdynamiccapsule.exe",
        "windowsdynamiccapsule.dll",
        "assets/square44x44logo.png",
        "assets/square150x150logo.png"
    )
    foreach ($requiredEntry in $requiredEntries) {
        if (-not $entries.ContainsKey($requiredEntry)) {
            throw "MSIX package is missing $requiredEntry."
        }
    }

    $manifestEntry = $entries["appxmanifest.xml"]
    $manifestStream = $manifestEntry.Open()
    $manifestReader = New-Object IO.StreamReader($manifestStream)
    try {
        [xml] $manifest = $manifestReader.ReadToEnd()
    }
    finally {
        $manifestReader.Dispose()
        $manifestStream.Dispose()
    }

    $hasSignature = $entries.ContainsKey("appxsignature.p7x")
    if ($RequireSignature -and -not $hasSignature) {
        throw "MSIX package does not contain AppxSignature.p7x."
    }
}
finally {
    $archive.Dispose()
}

$authenticode = Get-AuthenticodeSignature `
    -LiteralPath $resolvedPackagePath
$hash = Get-FileHash `
    -LiteralPath $resolvedPackagePath `
    -Algorithm SHA256
$signerSubject = if ($null -eq $authenticode.SignerCertificate) {
    $null
}
else {
    $authenticode.SignerCertificate.Subject
}

if ($RequireSignature -and $authenticode.Status -ne "Valid") {
    throw (
        "MSIX Authenticode signature is not valid: " +
        $authenticode.StatusMessage)
}

[PSCustomObject]@{
    Package = $resolvedPackagePath
    Sha256 = $hash.Hash
    IdentityName = $manifest.Package.Identity.Name
    Publisher = $manifest.Package.Identity.Publisher
    Version = $manifest.Package.Identity.Version
    Architecture = $manifest.Package.Identity.ProcessorArchitecture
    HasPackageSignature = $hasSignature
    SignatureStatus = $authenticode.Status.ToString()
    SignerSubject = $signerSubject
    RequiredEntriesPresent = $true
}
