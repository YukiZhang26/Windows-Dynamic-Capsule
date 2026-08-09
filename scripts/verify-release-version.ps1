[CmdletBinding()]
param(
    [switch] $RequireCandidateArtifact
)

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

$releaseVersion = Read-RepositoryText (
    "packaging\release-version.json") | ConvertFrom-Json
$storeTemplate = Read-RepositoryText (
    "packaging\store-submission.template.json") | ConvertFrom-Json
[xml] $project = Read-RepositoryText (
    "src\DynamicCapsule\DynamicCapsule.csproj")
[xml] $applicationManifest = Read-RepositoryText (
    "src\DynamicCapsule\app.manifest")
$storeBuildScript = Read-RepositoryText "scripts\build-store-msix.ps1"
$genericBuildScript = Read-RepositoryText "scripts\build-msix.ps1"

$releasePropertyNames = @($releaseVersion.PSObject.Properties.Name)
$requiredReleaseProperties = @(
    "schemaVersion"
    "productVersion"
    "msixVersion"
    "releaseTag"
    "testedUpgradeBaseline")
$contractShapeValid =
    $releaseVersion.schemaVersion -eq 1 -and
    @($requiredReleaseProperties | Where-Object {
            $_ -notin $releasePropertyNames
        }).Count -eq 0

$productVersion = [string]$releaseVersion.productVersion
$msixVersion = [string]$releaseVersion.msixVersion
$releaseTag = [string]$releaseVersion.releaseTag
$upgradeBaseline = [string]$releaseVersion.testedUpgradeBaseline
$productVersionValid = $productVersion -match '^\d+\.\d+\.\d+$'
$msixVersionValid = $msixVersion -match '^\d{1,5}(\.\d{1,5}){3}$'
$releaseTagValid = $releaseTag -match '^v\d+\.\d+\.\d+$'
$upgradeBaselineValid = $upgradeBaseline -match '^\d{1,5}(\.\d{1,5}){3}$'
$expectedMsixVersion = if ($productVersionValid) {
    "$productVersion.0"
}
else {
    $null
}
$versionsMap =
    $contractShapeValid -and
    $productVersionValid -and
    $msixVersionValid -and
    $releaseTagValid -and
    $upgradeBaselineValid -and
    $msixVersion -ceq $expectedMsixVersion -and
    $releaseTag -ceq "v$productVersion"

$projectVersionNode = $project.SelectSingleNode(
    "/Project/PropertyGroup/Version")
$assemblyVersionNode = $project.SelectSingleNode(
    "/Project/PropertyGroup/AssemblyVersion")
$fileVersionNode = $project.SelectSingleNode(
    "/Project/PropertyGroup/FileVersion")
$informationalVersionNode = $project.SelectSingleNode(
    "/Project/PropertyGroup/InformationalVersion")
$includeRevisionNode = $project.SelectSingleNode(
    "/Project/PropertyGroup/IncludeSourceRevisionInInformationalVersion")
$productNameNode = $project.SelectSingleNode(
    "/Project/PropertyGroup/Product")
$companyNode = $project.SelectSingleNode(
    "/Project/PropertyGroup/Company")
$projectVersionsMatch =
    $null -ne $projectVersionNode -and
    $null -ne $assemblyVersionNode -and
    $null -ne $fileVersionNode -and
    $null -ne $informationalVersionNode -and
    $null -ne $includeRevisionNode -and
    $null -ne $productNameNode -and
    $null -ne $companyNode -and
    $projectVersionNode.InnerText -ceq $productVersion -and
    $assemblyVersionNode.InnerText -ceq $msixVersion -and
    $fileVersionNode.InnerText -ceq $msixVersion -and
    $informationalVersionNode.InnerText -ceq $productVersion -and
    $includeRevisionNode.InnerText -ceq "false" -and
    $productNameNode.InnerText -ceq "Windows Dynamic Capsule" -and
    $companyNode.InnerText -ceq "Yuki Zhang"

$manifestNamespace = New-Object Xml.XmlNamespaceManager(
    $applicationManifest.NameTable)
$manifestNamespace.AddNamespace(
    "asmv1",
    "urn:schemas-microsoft-com:asm.v1")
$assemblyIdentity = $applicationManifest.SelectSingleNode(
    "/asmv1:assembly/asmv1:assemblyIdentity",
    $manifestNamespace)
$applicationManifestMatches =
    $null -ne $assemblyIdentity -and
    $assemblyIdentity.version -ceq $msixVersion

$storeTemplateMatches =
    [string]$storeTemplate.packageVersion -ceq $msixVersion
$storeBuildEnforcesContract =
    $storeBuildScript.Contains("release-version.json") -and
    $storeBuildScript.Contains(
        "Store packageVersion must match packaging\release-version.json") -and
    $storeBuildScript.Contains("RequireCleanRepository = `$true")
$genericBuildDefaultMatches = $genericBuildScript.Contains(
    ('[string] $PackageVersion = "' + $msixVersion + '"'))

$upgradeBaselineIsOlder = $false
if ($msixVersionValid -and $upgradeBaselineValid) {
    $upgradeBaselineIsOlder =
        [version]$upgradeBaseline -lt [version]$msixVersion
}

$localStoreConfigPath = Join-Path $repositoryRoot (
    "packaging\store-submission.json")
$localStoreConfigPresent = Test-Path `
    -LiteralPath $localStoreConfigPath `
    -PathType Leaf
$localStoreConfigMatches = $true
if ($localStoreConfigPresent) {
    $localStoreConfig = Get-Content `
        -LiteralPath $localStoreConfigPath `
        -Raw `
        -Encoding utf8 |
        ConvertFrom-Json
    $localStoreConfigMatches =
        [string]$localStoreConfig.packageVersion -ceq $msixVersion
}

$candidateMetadataPath = Join-Path $repositoryRoot (
    "artifacts\msix\WindowsDynamicCapsule_{0}_x64.unsigned.msix.metadata.json" `
        -f $msixVersion)
$candidatePackagePath = Join-Path $repositoryRoot (
    "artifacts\msix\WindowsDynamicCapsule_{0}_x64.unsigned.msix" `
        -f $msixVersion)
$candidateMetadataPresent = Test-Path `
    -LiteralPath $candidateMetadataPath `
    -PathType Leaf
$candidatePackagePresent = Test-Path `
    -LiteralPath $candidatePackagePath `
    -PathType Leaf
$candidateArtifactValid = -not $RequireCandidateArtifact
$candidateSourceCommit = $null
$currentSourceCommit = $null
if ($candidateMetadataPresent -or $candidatePackagePresent -or
    $RequireCandidateArtifact) {
    $candidateArtifactValid =
        $candidateMetadataPresent -and $candidatePackagePresent
    if ($candidateArtifactValid) {
        $candidateMetadata = Get-Content `
            -LiteralPath $candidateMetadataPath `
            -Raw `
            -Encoding utf8 |
            ConvertFrom-Json
        $candidateMetadataPropertyNames = @(
            $candidateMetadata.PSObject.Properties.Name)
        $candidateHasProvenance =
            "sourceCommit" -in $candidateMetadataPropertyNames -and
            "sourceDirty" -in $candidateMetadataPropertyNames
        if ($candidateHasProvenance) {
            $candidateSourceCommit = [string]$candidateMetadata.sourceCommit
        }
        $packageHash = Get-FileHash `
            -LiteralPath $candidatePackagePath `
            -Algorithm SHA256

        $gitCommand = Get-Command git -ErrorAction SilentlyContinue
        if ($null -ne $gitCommand) {
            $commitOutput = @(
                & $gitCommand.Source `
                    -c "safe.directory=$repositoryRoot" `
                    -C $repositoryRoot `
                    rev-parse HEAD 2>$null)
            if ($LASTEXITCODE -eq 0 -and $commitOutput.Count -eq 1) {
                $currentSourceCommit = $commitOutput[0].Trim().ToLowerInvariant()
            }
        }

        $candidateArtifactValid =
            $candidateMetadata.schemaVersion -eq 2 -and
            $candidateHasProvenance -and
            [string]$candidateMetadata.packageVersion -ceq $msixVersion -and
            $candidateMetadata.signed -eq $false -and
            (-not [bool]$candidateMetadata.sourceDirty) -and
            $candidateSourceCommit -match '^[0-9a-f]{40}$' -and
            $candidateSourceCommit -ceq $currentSourceCommit -and
            [string]$candidateMetadata.sha256 -ceq $packageHash.Hash
    }
}

$checks = @(
    [PSCustomObject]@{
        Name = "Release version contract is complete and well formed"
        Passed = $contractShapeValid -and $versionsMap
    },
    [PSCustomObject]@{
        Name = "Product, MSIX, and tag versions map exactly"
        Passed = $versionsMap
    },
    [PSCustomObject]@{
        Name = "Application assembly versions match the release"
        Passed = $projectVersionsMatch
    },
    [PSCustomObject]@{
        Name = "Application manifest version matches the release"
        Passed = $applicationManifestMatches
    },
    [PSCustomObject]@{
        Name = "Store template version matches the release"
        Passed = $storeTemplateMatches
    },
    [PSCustomObject]@{
        Name = "Store build enforces version and clean provenance"
        Passed = $storeBuildEnforcesContract
    },
    [PSCustomObject]@{
        Name = "Generic package default matches the release"
        Passed = $genericBuildDefaultMatches
    },
    [PSCustomObject]@{
        Name = "Tested upgrade baseline is older than the release"
        Passed = $upgradeBaselineIsOlder
    },
    [PSCustomObject]@{
        Name = "Local Store config matches when present"
        Passed = $localStoreConfigMatches
    },
    [PSCustomObject]@{
        Name = "Store candidate matches current source when present"
        Passed = $candidateArtifactValid
    }
)

$failedChecks = @($checks | Where-Object { -not $_.Passed })
$checks | Format-Table -AutoSize
if ($failedChecks.Count -gt 0) {
    throw "$($failedChecks.Count) release version check(s) failed."
}

[PSCustomObject]@{
    Passed = $true
    CheckCount = $checks.Count
    ProductVersion = $productVersion
    MsixVersion = $msixVersion
    ReleaseTag = $releaseTag
    TestedUpgradeBaseline = $upgradeBaseline
    LocalStoreConfigChecked = $localStoreConfigPresent
    CandidateArtifactChecked =
        $candidateMetadataPresent -or $candidatePackagePresent
    CandidateSourceCommit = $candidateSourceCommit
    CurrentSourceCommit = $currentSourceCommit
    SystemStateModified = $false
}
