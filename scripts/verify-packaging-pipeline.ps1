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
$directReleaseVerifyScript = Read-Source (
    "scripts\verify-direct-release-msix.ps1")
$storeBuildScript = Read-Source "scripts\build-store-msix.ps1"
$localInstallScript = Read-Source "scripts\install-local-test-msix.ps1"
$prepareWackScript = Read-Source "scripts\prepare-wack.ps1"
$wackScript = Read-Source "scripts\run-wack.ps1"
$storeConfigTemplate = Read-Source (
    "packaging\store-submission.template.json")
$signPathArtifactConfiguration = Read-Source (
    "packaging\signpath-artifact-configuration.xml")

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
        Name = "Store local test builds retain production identity"
        Passed =
            $storeBuildScript.Contains(
                "LocalTestCertificateThumbprint") -and
            $storeBuildScript.Contains(
                "LocalTestingOnly") -and
            $storeBuildScript.Contains(
                "do not upload it to Partner ") -and
            $storeBuildScript.Contains(
                '$metadata.identityName -ne $identityName')
    },
    [PSCustomObject]@{
        Name = "Local install verifies identity, version, and settings"
        Passed =
            $localInstallScript.Contains("MSIX SHA-256 does not match") -and
            $localInstallScript.Contains("Package downgrade is not allowed") -and
            $localInstallScript.Contains("AllowSameVersionReinstall") -and
            $localInstallScript.Contains("PreflightOnly") -and
            $localInstallScript.Contains("systemStateModified") -and
            $localInstallScript.Contains("-RequireSignature") -and
            $localInstallScript.Contains("settingsHashBefore") -and
            $localInstallScript.Contains("settingsPreserved") -and
            $localInstallScript.Contains("certificateRolledBack")
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
        Name = "Store candidates require clean Git provenance"
        Passed =
            $buildScript.Contains("RequireCleanRepository") -and
            $buildScript.Contains("status --porcelain") -and
            $buildScript.Contains("sourceCommit") -and
            $buildScript.Contains("sourceDirty") -and
            $storeBuildScript.Contains(
                "RequireCleanRepository = `$true")
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
        Name = "Development signing still verifies signer, type, and timestamp"
        Passed =
            $buildScript.Contains("Assert-CodeSignature") -and
            $buildScript.Contains("ExpectedThumbprint") -and
            $buildScript.Contains('SignatureType -ne "Authenticode"') -and
            $buildScript.Contains("TimeStamperCertificate") -and
            $buildScript.Contains(
                '$signature.Status -eq "UnknownError"') -and
            $buildScript.Contains("X509ChainStatusFlags]::UntrustedRoot") -and
            $buildScript.Contains('$chainStatuses.Count -eq 1') -and
            $buildScript.Contains(
                "AllowUntrustedDevelopmentCertificate") -and
            $buildScript.Contains(
                'if (-not $AllowUntrustedDevelopmentCertificate)')
    },
    [PSCustomObject]@{
        Name = "MSIX verification checks block map and signature"
        Passed =
            $verifyScript.Contains("appxblockmap.xml") -and
            $verifyScript.Contains("appxsignature.p7x") -and
            $verifyScript.Contains("Get-AuthenticodeSignature")
    },
    [PSCustomObject]@{
        Name = "Direct release rejects untrusted or mismatched signing"
        Passed =
            $directReleaseVerifyScript.Contains(
                "PublicReleaseEligible") -and
            $directReleaseVerifyScript.Contains(
                "Self-signed certificates are not permitted") -and
            $directReleaseVerifyScript.Contains(
                "X509RevocationMode]::Online") -and
            $directReleaseVerifyScript.Contains(
                "TimeStamperCertificate") -and
            $directReleaseVerifyScript.Contains(
                "TimestampChainRoot") -and
            $directReleaseVerifyScript.Contains(
                "WindowsDynamicCapsule.exe inside the MSIX") -and
            $directReleaseVerifyScript.Contains(
                '$signer.Subject -cne $ExpectedPublisher')
    },
    [PSCustomObject]@{
        Name = "SignPath deep signing is limited to project-owned binaries"
        Passed =
            $signPathArtifactConfiguration.Contains("<zip-file>") -and
            $signPathArtifactConfiguration.Contains("<msix-file") -and
            $signPathArtifactConfiguration.Contains(
                'path="WindowsDynamicCapsule.exe"') -and
            $signPathArtifactConfiguration.Contains(
                'path="WindowsDynamicCapsule.dll"') -and
            -not $signPathArtifactConfiguration.Contains(
                '<pe-file-set>') -and
            @(
                [regex]::Matches(
                    $signPathArtifactConfiguration,
                    '<authenticode-sign\s*/>')
            ).Count -eq 3
    },
    [PSCustomObject]@{
        Name = "WACK preparation pins and verifies the Microsoft installer"
        Passed =
            $prepareWackScript.Contains("10.0.28000.2526") -and
            $prepareWackScript.Contains(
                "02988EA51EAB2A2DB53E19735E51C97A6D221ADA74B9174FA0868870B9403BA0") -and
            $prepareWackScript.Contains("Get-AuthenticodeSignature") -and
            $prepareWackScript.Contains("Microsoft Corporation") -and
            $prepareWackScript.Contains("LaunchInstaller") -and
            -not $prepareWackScript.Contains("/quiet")
    },
    [PSCustomObject]@{
        Name = "WACK runner validates the installed Store identity"
        Passed =
            $wackScript.Contains(
                "Windows App Certification Kit must run from an elevated") -and
            $wackScript.Contains("appcert.exe") -and
            $wackScript.Contains("-packagefullname") -and
            $wackScript.Contains("-reportoutputpath") -and
            $wackScript.Contains("reset")
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
