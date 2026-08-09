[CmdletBinding()]
param(
    [string] $InstallerPath,

    [switch] $LaunchInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$sdkVersion = "10.0.28000.2526"
$installerUri = (
    "https://download.microsoft.com/download/" +
    "06fc99ac-527e-451e-a536-8866695a2e7e/" +
    "KIT_BUNDLE_WINDOWSSDK_MEDIACREATION/winsdksetup.exe")
$expectedSha256 = (
    "02988EA51EAB2A2DB53E19735E51C97A6D221ADA74B9174FA0868870B9403BA0")
$expectedProductVersion = "10.1.28000.2526"
$appCertPath = (
    "C:\Program Files (x86)\Windows Kits\10\" +
    "App Certification Kit\appcert.exe")

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $InstallerPath = Join-Path $repositoryRoot (
        "artifacts\tools\windows-sdk-$sdkVersion\winsdksetup.exe")
}

$resolvedInstallerPath = [IO.Path]::GetFullPath($InstallerPath)
$resolvedAppCertPath = [IO.Path]::GetFullPath($appCertPath)
if (Test-Path -LiteralPath $resolvedAppCertPath -PathType Leaf) {
    [PSCustomObject]@{
        WackInstalled = $true
        AppCert = $resolvedAppCertPath
        InstallerDownloaded = $false
        InstallerLaunched = $false
        SdkVersion = $sdkVersion
    }
    return
}

$installerDirectory = Split-Path -Parent $resolvedInstallerPath
New-Item -ItemType Directory -Path $installerDirectory -Force |
    Out-Null

$downloaded = $false
if (-not (Test-Path -LiteralPath $resolvedInstallerPath -PathType Leaf)) {
    Invoke-WebRequest `
        -UseBasicParsing `
        -Uri $installerUri `
        -OutFile $resolvedInstallerPath
    $downloaded = $true
}

$hash = Get-FileHash `
    -Algorithm SHA256 `
    -LiteralPath $resolvedInstallerPath
if (-not [string]::Equals(
        $hash.Hash,
        $expectedSha256,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw (
        "Windows SDK installer hash mismatch. Expected " +
        "$expectedSha256, received $($hash.Hash).")
}

$signature = Get-AuthenticodeSignature -FilePath $resolvedInstallerPath
if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
    $null -eq $signature.SignerCertificate -or
    $signature.SignerCertificate.Subject -cne (
        "CN=Microsoft Corporation, O=Microsoft Corporation, " +
        "L=Redmond, S=Washington, C=US")) {
    throw (
        "Windows SDK installer does not have the expected valid " +
        "Microsoft Authenticode signature.")
}

$versionInfo = (Get-Item -LiteralPath $resolvedInstallerPath).VersionInfo
if ($versionInfo.ProductVersion -cne $expectedProductVersion) {
    throw (
        "Windows SDK installer version mismatch. Expected " +
        "$expectedProductVersion, received $($versionInfo.ProductVersion).")
}

$launched = $false
if ($LaunchInstaller) {
    Write-Host (
        "In Windows SDK Setup, select only Windows App Certification " +
        "Kit unless another SDK component is intentionally required.")
    Write-Host (
        "Do not continue if the displayed version is not $sdkVersion.")
    Start-Process -FilePath $resolvedInstallerPath
    $launched = $true
}

[PSCustomObject]@{
    WackInstalled = $false
    AppCert = $resolvedAppCertPath
    Installer = $resolvedInstallerPath
    InstallerDownloaded = $downloaded
    InstallerVerified = $true
    InstallerLaunched = $launched
    SdkVersion = $sdkVersion
    Sha256 = $hash.Hash
    NextStep = if ($launched) {
        "Finish the interactive WACK-only installation, then run " +
        "scripts\run-wack.ps1 from elevated PowerShell."
    }
    else {
        "Review this result, then rerun with -LaunchInstaller when " +
        "interactive installation is approved."
    }
}
