[CmdletBinding()]
param(
    [string] $PackageIdentityName =
        "YukiZhang.WindowsDynamicCapsule",

    [string] $ReportPath,

    [string] $AppCertPath =
        "C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe",

    [switch] $SkipReset
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$principal = New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw (
        "Windows App Certification Kit must run from an elevated " +
        "interactive PowerShell session.")
}

$resolvedAppCertPath = [IO.Path]::GetFullPath($AppCertPath)
if (-not (Test-Path -LiteralPath $resolvedAppCertPath -PathType Leaf)) {
    throw (
        "Windows App Certification Kit was not found: " +
        "$resolvedAppCertPath`nInstall the current stable Windows SDK, " +
        "including Windows App Certification Kit, before running this " +
        "script.")
}

$package = Get-AppxPackage -Name $PackageIdentityName |
    Sort-Object Version -Descending |
    Select-Object -First 1
if ($null -eq $package) {
    throw "Installed package was not found: $PackageIdentityName"
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $repositoryRoot (
        "artifacts\wack\WindowsDynamicCapsule-{0}.xml" -f
            $package.Version)
}

$resolvedReportPath = [IO.Path]::GetFullPath($ReportPath)
$reportDirectory = Split-Path -Parent $resolvedReportPath
if (-not [string]::IsNullOrWhiteSpace($reportDirectory)) {
    New-Item -ItemType Directory -Path $reportDirectory -Force |
        Out-Null
}

if (-not $SkipReset) {
    & $resolvedAppCertPath reset
    if ($LASTEXITCODE -ne 0) {
        throw "Windows App Certification Kit reset failed."
    }
}

& $resolvedAppCertPath test `
    -packagefullname $package.PackageFullName `
    -reportoutputpath $resolvedReportPath
if ($LASTEXITCODE -ne 0) {
    throw (
        "Windows App Certification Kit returned exit code " +
        "$LASTEXITCODE. Review the generated report if present: " +
        $resolvedReportPath)
}

if (-not (Test-Path -LiteralPath $resolvedReportPath -PathType Leaf)) {
    throw "WACK completed without creating the expected report."
}

[PSCustomObject]@{
    Passed = $true
    PackageFullName = $package.PackageFullName
    PackageVersion = $package.Version.ToString()
    Report = $resolvedReportPath
    AppCert = $resolvedAppCertPath
}
