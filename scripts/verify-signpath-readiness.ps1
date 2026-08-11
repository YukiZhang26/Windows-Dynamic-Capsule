[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot

function Read-RepositoryText {
    param([Parameter(Mandatory = $true)][string] $RelativePath)

    Get-Content `
        -LiteralPath (Join-Path $repositoryRoot $RelativePath) `
        -Raw `
        -Encoding utf8
}

$workflow = Read-RepositoryText (
    ".github\workflows\signpath-release.yml")
$artifactConfiguration = Read-RepositoryText (
    "packaging\signpath-artifact-configuration.xml")
$application = Read-RepositoryText "SIGNPATH_APPLICATION.md"
$policy = Read-RepositoryText "CODE_SIGNING.md"
$readmeEnglish = Read-RepositoryText "README.md"
$readmeSimplifiedChinese = Read-RepositoryText "README.zh-CN.md"
$readmeTraditionalChinese = Read-RepositoryText "README.zh-TW.md"

$checks = @(
    [PSCustomObject]@{
        Name = "Signing workflow is manual only"
        Passed =
            $workflow.Contains("workflow_dispatch:") -and
            -not $workflow.Contains("push:") -and
            -not $workflow.Contains("pull_request:") -and
            -not $workflow.Contains("schedule:")
    },
    [PSCustomObject]@{
        Name = "Signing build uses a GitHub-hosted Windows runner"
        Passed = $workflow.Contains("runs-on: windows-latest")
    },
    [PSCustomObject]@{
        Name = "Official artifact and SignPath actions are pinned"
        Passed =
            @(
                [regex]::Matches(
                    $workflow,
                    "actions/upload-artifact@v7")
            ).Count -eq 2 -and
            $workflow.Contains(
                "signpath/github-action-submit-signing-request@v2")
    },
    [PSCustomObject]@{
        Name = "Unsigned artifact is uploaded before signing"
        Passed =
            $workflow.IndexOf("id: upload-unsigned-artifact") -ge 0 -and
            $workflow.IndexOf("id: signpath") -gt
                $workflow.IndexOf("id: upload-unsigned-artifact") -and
            $workflow.Contains(
                "github-artifact-id: `${{ steps.upload-unsigned-artifact.outputs.artifact-id }}")
    },
    [PSCustomObject]@{
        Name = "Acceptance values stay in variables and secrets"
        Passed =
            $workflow.Contains("vars.SIGNPATH_ORGANIZATION_ID") -and
            $workflow.Contains("vars.SIGNPATH_PROJECT_SLUG") -and
            $workflow.Contains("vars.SIGNPATH_SIGNING_POLICY_SLUG") -and
            $workflow.Contains(
                "vars.SIGNPATH_ARTIFACT_CONFIGURATION_SLUG") -and
            $workflow.Contains("vars.DIRECT_MSIX_IDENTITY") -and
            $workflow.Contains("vars.DIRECT_MSIX_PUBLISHER") -and
            $workflow.Contains(
                "vars.DIRECT_MSIX_PUBLISHER_DISPLAY_NAME") -and
            $workflow.Contains("secrets.SIGNPATH_API_TOKEN")
    },
    [PSCustomObject]@{
        Name = "Signing starts from an exact clean release tag"
        Passed =
            $workflow.Contains("git describe --tags --exact-match HEAD") -and
            $workflow.Contains("git status --porcelain") -and
            $workflow.Contains("-RequireCleanRepository") -and
            $workflow.Contains("release-version.json")
    },
    [PSCustomObject]@{
        Name = "Signed artifact receives strict direct-release validation"
        Passed =
            $workflow.Contains("verify-direct-release-msix.ps1") -and
            $workflow.Contains("PublicReleaseEligible") -and
            $workflow.Contains("release-proof.json") -and
            $workflow.Contains("SHA256SUMS.txt")
    },
    [PSCustomObject]@{
        Name = "Workflow never publishes a GitHub Release automatically"
        Passed =
            -not $workflow.Contains("gh release create") -and
            -not $workflow.Contains("softprops/action-gh-release") -and
            -not $workflow.Contains("actions/create-release")
    },
    [PSCustomObject]@{
        Name = "Artifact configuration constrains project metadata"
        Passed =
            $artifactConfiguration.Contains(
                'product-name="Windows Dynamic Capsule"') -and
            $artifactConfiguration.Contains(
                'product-version="${productVersion}"') -and
            $artifactConfiguration.Contains(
                'file-version="${msixVersion}"') -and
            $artifactConfiguration.Contains('company-name="Yuki Zhang"')
    },
    [PSCustomObject]@{
        Name = "Application is honest about pending provider acceptance"
        Passed =
            $application.Contains("preparation only") -and
            $application.Contains("has not been accepted") -and
            $application.Contains("Pending Store certification") -and
            $application.Contains("SIGNPATH_API_TOKEN")
    },
    [PSCustomObject]@{
        Name = "Signing policy documents roles, privacy, and attribution"
        Passed =
            $policy.Contains("## Team roles") -and
            $policy.Contains("## Privacy and provenance") -and
            $policy.Contains("SIGNPATH_APPLICATION.md") -and
            $policy.Contains("Free code signing provided by")
    },
    [PSCustomObject]@{
        Name = "All README variants document removal"
        Passed =
            $readmeEnglish.Contains("WindowsDynamicCapsule.exe") -and
            $readmeEnglish.Contains(
                "%LOCALAPPDATA%\WindowsDynamicCapsule") -and
            $readmeSimplifiedChinese.Contains(
                "WindowsDynamicCapsule.exe") -and
            $readmeSimplifiedChinese.Contains(
                "%LOCALAPPDATA%\WindowsDynamicCapsule") -and
            $readmeTraditionalChinese.Contains(
                "WindowsDynamicCapsule.exe") -and
            $readmeTraditionalChinese.Contains(
                "%LOCALAPPDATA%\WindowsDynamicCapsule")
    }
)

$failedChecks = @($checks | Where-Object { -not $_.Passed })
$checks | Format-Table -AutoSize
if ($failedChecks.Count -gt 0) {
    throw "$($failedChecks.Count) SignPath readiness check(s) failed."
}

[PSCustomObject]@{
    Passed = $true
    CheckCount = $checks.Count
    ExternalAccountConfigurationRequired = $true
    SystemStateModified = $false
}
