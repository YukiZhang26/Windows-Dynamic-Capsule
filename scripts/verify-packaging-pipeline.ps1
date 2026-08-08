[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot

function Read-Source {
    param([Parameter(Mandatory = $true)][string] $RelativePath)

    return Get-Content `
        -LiteralPath (Join-Path $repositoryRoot $RelativePath) `
        -Raw `
        -Encoding utf8
}

$template = Read-Source "packaging\Package.appxmanifest.template"
$toolProject = Read-Source (
    "packaging\tools\WindowsSdkBuildTools.csproj")
$commonScript = Read-Source "packaging\Packaging.Common.ps1"
$buildScript = Read-Source "scripts\build-msix.ps1"
$verifyScript = Read-Source "scripts\verify-msix.ps1"
$storeBuildScript = Read-Source "scripts\build-store-msix.ps1"
$storeConfigTemplate = Read-Source (
    "packaging\store-submission.template.json")

$signExeIndex = $buildScript.IndexOf(
    '"WindowsDynamicCapsule.exe"')
$packIndex = $buildScript.IndexOf(
    '& $makeAppxPath pack')

$checks = @(
    [PSCustomObject]@{
        Name = "Manifest identity is parameterized"
        Passed =
            $template.Contains("{{IDENTITY_NAME}}") -and
            $template.Contains("{{PUBLISHER}}") -and
            $template.Contains("{{VERSION}}") -and
            $template.Contains("{{ARCHITECTURE}}")
    },
    [PSCustomObject]@{
        Name = "Manifest declares full trust and notification capability"
        Passed =
            $template.Contains('Name="runFullTrust"') -and
            $template.Contains('Name="userNotificationListener"')
    },
    [PSCustomObject]@{
        Name = "Manifest declares packaged startup task"
        Passed =
            $template.Contains(
                'Category="windows.startupTask"') -and
            $template.Contains(
                'TaskId="DynamicCapsuleStartup"')
    },
    [PSCustomObject]@{
        Name = "Store build requires Partner Center identity"
        Passed =
            $storeBuildScript.Contains(
                '"PASTE_"') -and
            $storeBuildScript.Contains(
                "PackageVersion") -and
            $storeConfigTemplate.Contains(
                "PASTE_PACKAGE_IDENTITY_NAME_FROM_PARTNER_CENTER") -and
            $storeConfigTemplate.Contains(
                "PASTE_PACKAGE_PUBLISHER_FROM_PARTNER_CENTER")
    },
    [PSCustomObject]@{
        Name = "Windows SDK tools are version pinned"
        Passed =
            $toolProject.Contains(
                'Microsoft.Windows.SDK.BuildTools') -and
            $toolProject.Contains('10.0.28000.2526')
    },
    [PSCustomObject]@{
        Name = "Recursive cleanup is repository scoped"
        Passed =
            $commonScript.Contains("Assert-PathWithinRepository") -and
            $commonScript.Contains("Reset-RepositoryDirectory")
    },
    [PSCustomObject]@{
        Name = "Release is self-contained by default"
        Passed =
            $buildScript.Contains(
                '$selfContained = -not $FrameworkDependent') -and
            $buildScript.Contains("--self-contained")
    },
    [PSCustomObject]@{
        Name = "Publisher must exactly match certificate subject"
        Passed =
            $buildScript.Contains(
                "Manifest publisher must exactly match") -and
            $buildScript.Contains("[StringComparison]::Ordinal")
    },
    [PSCustomObject]@{
        Name = "Application binaries are signed before package creation"
        Passed =
            $signExeIndex -ge 0 -and
            $packIndex -gt $signExeIndex
    },
    [PSCustomObject]@{
        Name = "Signing uses SHA256 and optional RFC3161 timestamp"
        Passed =
            $commonScript.Contains('"SHA256"') -and
            $commonScript.Contains('"/tr"') -and
            $commonScript.Contains('"/td"')
    },
    [PSCustomObject]@{
        Name = "Certificate preflight requires private key and code-signing EKU"
        Passed =
            $commonScript.Contains("HasPrivateKey") -and
            $commonScript.Contains("1.3.6.1.5.5.7.3.3")
    },
    [PSCustomObject]@{
        Name = "MSIX verification checks block map and signature"
        Passed =
            $verifyScript.Contains("appxblockmap.xml") -and
            $verifyScript.Contains("appxsignature.p7x") -and
            $verifyScript.Contains("Get-AuthenticodeSignature")
    }
)

$failedChecks = @($checks | Where-Object { -not $_.Passed })
$checks | Format-Table -AutoSize
if ($failedChecks.Count -gt 0) {
    throw "$($failedChecks.Count) packaging pipeline check(s) failed."
}

[PSCustomObject]@{
    Passed = $true
    CheckCount = $checks.Count
    CertificateStoreModified = $false
    FilesSigned = 0
}
