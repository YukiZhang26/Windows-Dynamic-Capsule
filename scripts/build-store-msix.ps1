[CmdletBinding()]
param(
    [string] $ConfigPath,

    [switch] $SkipRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $PSScriptRoot (
        "..\packaging\store-submission.json")
}

function Get-RequiredConfigString {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Config,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $property = $Config.PSObject.Properties[$Name]
    if ($null -eq $property) {
        throw "Store submission config is missing '$Name'."
    }

    $value = [string]$property.Value
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Store submission config '$Name' is empty."
    }

    if ($value.StartsWith(
            "PASTE_",
            [StringComparison]::OrdinalIgnoreCase)) {
        throw (
            "Store submission config '$Name' still contains its " +
            "Partner Center placeholder: $value")
    }

    return $value.Trim()
}

$resolvedConfigPath = [IO.Path]::GetFullPath($ConfigPath)
if (-not (Test-Path -LiteralPath $resolvedConfigPath -PathType Leaf)) {
    throw (
        "Store submission config was not found: $resolvedConfigPath`n" +
        "Copy packaging\store-submission.template.json to " +
        "packaging\store-submission.json and paste the exact values " +
        "from Partner Center.")
}

$config = Get-Content `
    -LiteralPath $resolvedConfigPath `
    -Raw `
    -Encoding utf8 |
    ConvertFrom-Json

$identityName = Get-RequiredConfigString `
    -Config $config `
    -Name "identityName"
$publisher = Get-RequiredConfigString `
    -Config $config `
    -Name "publisher"
$publisherDisplayName = Get-RequiredConfigString `
    -Config $config `
    -Name "publisherDisplayName"
$packageVersion = Get-RequiredConfigString `
    -Config $config `
    -Name "packageVersion"

if ($identityName -notmatch '^[A-Za-z0-9.-]{3,50}$') {
    throw "Partner Center identityName has an invalid MSIX format."
}

if ($packageVersion -notmatch '^\d{1,5}(\.\d{1,5}){3}$') {
    throw "packageVersion must contain four numeric parts."
}

$versionParts = @($packageVersion.Split('.') | ForEach-Object {
    [int]$_
})
if (@($versionParts | Where-Object { $_ -gt 65535 }).Count -gt 0) {
    throw "Each packageVersion part must be between 0 and 65535."
}

$buildScript = Join-Path $PSScriptRoot "build-msix.ps1"
$buildParameters = @{
    Configuration = "Release"
    RuntimeIdentifier = "win-x64"
    PackageVersion = $packageVersion
    IdentityName = $identityName
    Publisher = $publisher
    PublisherDisplayName = $publisherDisplayName
}
if ($SkipRestore) {
    $buildParameters.SkipRestore = $true
}

& $buildScript @buildParameters

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$metadataPath = Join-Path $repositoryRoot (
    "artifacts\msix\WindowsDynamicCapsule_{0}_x64.unsigned.msix.metadata.json" `
        -f $packageVersion)
$metadata = Get-Content `
    -LiteralPath $metadataPath `
    -Raw `
    -Encoding utf8 |
    ConvertFrom-Json

if (($metadata.identityName -ne $identityName) -or
    ($metadata.publisher -ne $publisher) -or
    ($metadata.packageVersion -ne $packageVersion) -or
    ($metadata.signed -ne $false)) {
    throw "Generated Store MSIX metadata does not match the config."
}

[PSCustomObject]@{
    Passed = $true
    Package = $metadata.package
    Sha256 = $metadata.sha256
    IdentityName = $metadata.identityName
    Publisher = $metadata.publisher
    Version = $metadata.packageVersion
    Signed = $metadata.signed
    StoreSigningRequired = $true
}
