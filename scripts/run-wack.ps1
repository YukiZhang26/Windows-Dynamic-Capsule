[CmdletBinding()]
param(
    [string] $PackageIdentityName =
        "YukiZhang.WindowsDynamicCapsule",

    [string] $PackagePath,

    [string] $ReportPath,

    [string] $AppCertPath,

    [switch] $SkipReset
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-AppCertPath {
    param([string] $RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = [IO.Path]::GetFullPath($RequestedPath)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "Windows App Certification Kit was not found: $resolved"
        }

        return $resolved
    }

    $kitRoots = [Collections.Generic.List[string]]::new()
    foreach ($registryPath in @(
            "HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots",
            "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots"
        )) {
        try {
            $kitsRoot = [string](Get-ItemProperty `
                    -LiteralPath $registryPath `
                    -ErrorAction Stop).KitsRoot10
            if (-not [string]::IsNullOrWhiteSpace($kitsRoot)) {
                $kitRoots.Add($kitsRoot)
            }
        }
        catch {
            # Continue with the other registry view and conventional paths.
        }
    }

    $kitRoots.Add("C:\Program Files (x86)\Windows Kits\10")
    $kitRoots.Add("C:\Program Files\Windows Kits\10")
    foreach ($root in @($kitRoots | Select-Object -Unique)) {
        $candidate = Join-Path $root (
            "App Certification Kit\appcert.exe")
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }

    throw (
        "Windows App Certification Kit was not found. Install the " +
        "current stable Windows SDK including the App Certification Kit, " +
        "or pass -AppCertPath explicitly.")
}

$principal = New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw (
        "Windows App Certification Kit must run from an elevated " +
        "interactive PowerShell session.")
}

$resolvedAppCertPath = Resolve-AppCertPath -RequestedPath $AppCertPath
$appCertSignature = Get-AuthenticodeSignature `
    -LiteralPath $resolvedAppCertPath
if ($appCertSignature.Status -ne
        [Management.Automation.SignatureStatus]::Valid -or
    $null -eq $appCertSignature.SignerCertificate -or
    $appCertSignature.SignerCertificate.Subject -cne (
        "CN=Microsoft Corporation, O=Microsoft Corporation, " +
        "L=Redmond, S=Washington, C=US")) {
    throw (
        "Windows App Certification Kit does not have the expected valid " +
        "Microsoft Authenticode signature: $resolvedAppCertPath")
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$package = $null
$resolvedPackagePath = $null
$targetType = "InstalledPackage"
if (-not [string]::IsNullOrWhiteSpace($PackagePath)) {
    $resolvedPackagePath = [IO.Path]::GetFullPath($PackagePath)
    if (-not (Test-Path `
            -LiteralPath $resolvedPackagePath `
            -PathType Leaf)) {
        throw "MSIX package was not found: $resolvedPackagePath"
    }
    if ([IO.Path]::GetExtension($resolvedPackagePath) -ine ".msix") {
        throw "PackagePath must point to an .msix package."
    }

    $targetType = "PackageFile"
    if ([string]::IsNullOrWhiteSpace($ReportPath)) {
        $reportName = [IO.Path]::GetFileNameWithoutExtension(
            $resolvedPackagePath)
        $ReportPath = Join-Path $repositoryRoot (
            "artifacts\wack\$reportName.xml")
    }
}
else {
    $package = Get-AppxPackage -Name $PackageIdentityName |
        Sort-Object Version -Descending |
        Select-Object -First 1
    if ($null -eq $package) {
        throw "Installed package was not found: $PackageIdentityName"
    }

    if ([string]::IsNullOrWhiteSpace($ReportPath)) {
        $ReportPath = Join-Path $repositoryRoot (
            "artifacts\wack\WindowsDynamicCapsule-{0}.xml" -f
                $package.Version)
    }
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

if ($null -ne $resolvedPackagePath) {
    & $resolvedAppCertPath test `
        -appxpackagepath $resolvedPackagePath `
        -reportoutputpath $resolvedReportPath
}
else {
    & $resolvedAppCertPath test `
        -packagefullname $package.PackageFullName `
        -reportoutputpath $resolvedReportPath
}
if ($LASTEXITCODE -ne 0) {
    throw (
        "Windows App Certification Kit returned exit code " +
        "$LASTEXITCODE. Review the generated report if present: " +
        $resolvedReportPath)
}

if (-not (Test-Path -LiteralPath $resolvedReportPath -PathType Leaf)) {
    throw "WACK completed without creating the expected report."
}

$reportXml = New-Object Xml.XmlDocument
$reportXml.PreserveWhitespace = $false
$reportXml.Load($resolvedReportPath)
$reportRoot = $reportXml.SelectSingleNode("/REPORT")
if ($null -eq $reportRoot) {
    throw "WACK report does not contain the expected REPORT root element."
}

$overallResult = [string]$reportRoot.GetAttribute("OVERALL_RESULT")
$partialRun = [string]$reportRoot.GetAttribute("PARTIAL_RUN")
$testResults = @(
    $reportXml.SelectNodes("//TEST") | ForEach-Object {
        $resultNode = $_.SelectSingleNode("RESULT")
        [PSCustomObject]@{
            Index = [string]$_.GetAttribute("INDEX")
            Name = [string]$_.GetAttribute("NAME")
            Optional = [string]$_.GetAttribute("OPTIONAL") -ieq "TRUE"
            Result = if ($null -ne $resultNode) {
                $resultNode.InnerText.Trim()
            }
            else {
                "MISSING"
            }
        }
    }
)
$failedTests = @($testResults | Where-Object { $_.Result -ine "PASS" })
if ($overallResult -ine "PASS" -or $partialRun -ieq "TRUE") {
    throw (
        "WACK report is not a complete pass: OVERALL_RESULT=" +
        "$overallResult, PARTIAL_RUN=$partialRun. Report: " +
        $resolvedReportPath)
}

[PSCustomObject]@{
    Passed = $true
    OverallResult = $overallResult
    PartialRun = $partialRun -ieq "TRUE"
    TestsPassed = @($testResults | Where-Object {
            $_.Result -ieq "PASS"
        }).Count
    TestsFailed = $failedTests.Count
    FailedTests = @($failedTests | ForEach-Object {
            "{0}: {1} (optional: {2})" -f
                $_.Index, $_.Name, $_.Optional
        })
    TargetType = $targetType
    PackagePath = $resolvedPackagePath
    PackageFullName = if ($null -ne $package) {
        $package.PackageFullName
    }
    else {
        $null
    }
    PackageVersion = if ($null -ne $package) {
        $package.Version.ToString()
    }
    else {
        $null
    }
    Report = $resolvedReportPath
    AppCert = $resolvedAppCertPath
}
