[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [ValidateSet("win-x64")]
    [string] $RuntimeIdentifier = "win-x64",

    [ValidatePattern('^\d{1,5}\.\d{1,5}\.\d{1,5}\.\d{1,5}$')]
    [string] $PackageVersion = "0.1.0.0",

    [ValidatePattern('^[A-Za-z0-9.-]{3,50}$')]
    [string] $IdentityName = "DynamicCapsule",

    [string] $Publisher,

    [string] $PublisherDisplayName = "Dynamic Capsule",

    [string] $CertificateThumbprint,

    [string] $TimestampUrl = "http://timestamp.digicert.com",

    [switch] $SkipTimestamp,

    [switch] $AllowUntrustedDevelopmentCertificate,

    [switch] $FrameworkDependent,

    [switch] $SkipRestore,

    [string] $WindowsSdkBuildToolsVersion = "10.0.28000.2526"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "..\packaging\Packaging.Common.ps1")
Add-Type -AssemblyName System.Drawing

function ConvertTo-XmlAttributeValue {
    param([Parameter(Mandatory = $true)][string] $Value)

    return [Security.SecurityElement]::Escape($Value)
}

function Draw-Pill {
    param(
        [Parameter(Mandatory = $true)]
        [System.Drawing.Graphics] $Graphics,

        [Parameter(Mandatory = $true)]
        [System.Drawing.Brush] $Brush,

        [single] $X,
        [single] $Y,
        [single] $Width,
        [single] $Height
    )

    $radius = $Height / 2
    $Graphics.FillRectangle(
        $Brush,
        $X + $radius,
        $Y,
        [Math]::Max(1, $Width - ($radius * 2)),
        $Height)
    $Graphics.FillEllipse($Brush, $X, $Y, $Height, $Height)
    $Graphics.FillEllipse(
        $Brush,
        $X + $Width - $Height,
        $Y,
        $Height,
        $Height)
}

function New-CapsuleLogo {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [int] $Width,

        [Parameter(Mandatory = $true)]
        [int] $Height
    )

    $bitmap = New-Object System.Drawing.Bitmap($Width, $Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode =
        [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.CompositingQuality =
        [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $pillHeight = [single]([Math]::Max(
        8,
        [Math]::Min($Width, $Height) * 0.34))
    $pillWidth = [single]([Math]::Max(
        $pillHeight * 2.2,
        $Width * 0.78))
    $pillWidth = [single]([Math]::Min($pillWidth, $Width * 0.86))
    $pillX = [single](($Width - $pillWidth) / 2)
    $pillY = [single](($Height - $pillHeight) / 2)

    $surfaceBrush = New-Object System.Drawing.SolidBrush(
        [System.Drawing.Color]::FromArgb(242, 22, 24, 29))
    $accentBrush = New-Object System.Drawing.SolidBrush(
        [System.Drawing.Color]::FromArgb(255, 42, 99, 245))
    $glyphBrush = New-Object System.Drawing.SolidBrush(
        [System.Drawing.Color]::White)

    try {
        Draw-Pill `
            -Graphics $graphics `
            -Brush $surfaceBrush `
            -X $pillX `
            -Y $pillY `
            -Width $pillWidth `
            -Height $pillHeight

        $dotSize = [single]($pillHeight * 0.58)
        $dotX = [single]($pillX + (($pillHeight - $dotSize) / 2))
        $dotY = [single]($pillY + (($pillHeight - $dotSize) / 2))
        $graphics.FillEllipse(
            $accentBrush,
            $dotX,
            $dotY,
            $dotSize,
            $dotSize)

        $barWidth = [single]([Math]::Max(1, $dotSize * 0.13))
        $barHeight = [single]($dotSize * 0.42)
        $barY = [single]($dotY + (($dotSize - $barHeight) / 2))
        $barGap = [single]($barWidth * 0.85)
        $barStartX = [single](
            $dotX + (($dotSize - (($barWidth * 3) + ($barGap * 2))) / 2))
        for ($index = 0; $index -lt 3; $index++) {
            $graphics.FillRectangle(
                $glyphBrush,
                $barStartX + (($barWidth + $barGap) * $index),
                $barY,
                $barWidth,
                $barHeight)
        }

        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $glyphBrush.Dispose()
        $accentBrush.Dispose()
        $surfaceBrush.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Invoke-CodeSign {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SignToolPath,

        [Parameter(Mandatory = $true)]
        [string] $Thumbprint,

        [Parameter(Mandatory = $true)]
        [string] $FilePath
    )

    $signArguments = Get-SignToolArguments `
        -Thumbprint $Thumbprint `
        -TimestampUrl $TimestampUrl `
        -SkipTimestamp:$SkipTimestamp `
        -FilePath $FilePath
    & $SignToolPath @signArguments
    if ($LASTEXITCODE -ne 0) {
        throw "SignTool failed for $FilePath."
    }
}

function Assert-CodeSignature {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FilePath,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedThumbprint,

        [switch] $AllowUntrusted,

        [switch] $RequireTimestamp
    )

    $signature = Get-AuthenticodeSignature -LiteralPath $FilePath
    if ($null -eq $signature.SignerCertificate) {
        throw "Signed file does not expose a signer certificate: $FilePath"
    }

    if (-not [string]::Equals(
            $signature.SignerCertificate.Thumbprint,
            $ExpectedThumbprint,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Signed file does not match the requested certificate: $FilePath"
    }

    if ($signature.SignatureType -ne "Authenticode") {
        throw (
            "Signed file does not contain an Authenticode signature: " +
            $FilePath)
    }

    if ($RequireTimestamp -and
        $null -eq $signature.TimeStamperCertificate) {
        throw "Signed file does not contain the required timestamp: $FilePath"
    }

    if ($signature.Status -eq "Valid") {
        return
    }

    $chain = New-Object Security.Cryptography.X509Certificates.X509Chain
    try {
        $chain.ChainPolicy.RevocationMode =
            [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
        $chainBuilt = $chain.Build($signature.SignerCertificate)
        $chainStatuses = @($chain.ChainStatus | ForEach-Object { $_.Status })
    }
    finally {
        $chain.Dispose()
    }

    $untrustedRoot =
        [Security.Cryptography.X509Certificates.X509ChainStatusFlags]::UntrustedRoot
    $isUntrustedRootOnly =
        (-not $chainBuilt) -and
        $chainStatuses.Count -eq 1 -and
        $chainStatuses[0] -eq $untrustedRoot
    $isExpectedDevelopmentTrustFailure =
        $AllowUntrusted -and
        $signature.Status -eq "UnknownError" -and
        $isUntrustedRootOnly
    if (-not $isExpectedDevelopmentTrustFailure) {
        throw (
            "Signed file did not verify: $FilePath " +
            "($($signature.Status))")
    }
}

$repositoryRoot = Get-RepositoryRoot
$dotnetPath = Get-RepositoryDotnet -RepositoryRoot $repositoryRoot
$projectPath = Join-Path $repositoryRoot (
    "src\DynamicCapsule\DynamicCapsule.csproj")
$templatePath = Join-Path $repositoryRoot (
    "packaging\Package.appxmanifest.template")
$nugetConfigPath = Join-Path $repositoryRoot "NuGet.Config"
$packagesPath = Join-Path $repositoryRoot ".nuget\packages"
$artifactsRoot = Join-Path $repositoryRoot "artifacts\msix"
$workRoot = Join-Path $artifactsRoot "work"
$publishDirectory = Join-Path $workRoot "publish"
$layoutDirectory = Join-Path $workRoot "layout"
$validationDirectory = Join-Path $workRoot "validation"

$versionParts = @($PackageVersion.Split('.') | ForEach-Object { [int] $_ })
if (@($versionParts | Where-Object { $_ -gt 65535 }).Count -gt 0) {
    throw "Each package version component must be between 0 and 65535."
}

$certificate = $null
$certificateChainTrusted = $false
if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    $certificate = Get-CodeSigningCertificate `
        -Thumbprint $CertificateThumbprint
    $CertificateThumbprint = $certificate.Thumbprint

    if ([string]::IsNullOrWhiteSpace($Publisher)) {
        $Publisher = $certificate.Subject
    }
    elseif (-not [string]::Equals(
            $Publisher,
            $certificate.Subject,
            [StringComparison]::Ordinal)) {
        throw (
            "Manifest publisher must exactly match the certificate subject. " +
            "Certificate subject: $($certificate.Subject)")
    }

    $chain = New-Object Security.Cryptography.X509Certificates.X509Chain
    $chain.ChainPolicy.RevocationMode =
        [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
    try {
        $certificateChainTrusted = $chain.Build($certificate)
        if ((-not $certificateChainTrusted) -and
            (-not $AllowUntrustedDevelopmentCertificate)) {
            $chainErrors = (
                $chain.ChainStatus |
                    ForEach-Object { $_.StatusInformation.Trim() }) -join "; "
            throw (
                "The certificate chain is not trusted: $chainErrors " +
                "Use an enterprise-approved certificate for this machine.")
        }
    }
    finally {
        $chain.Dispose()
    }
}
elseif ([string]::IsNullOrWhiteSpace($Publisher)) {
    $Publisher = "CN=Dynamic Capsule Development"
}

if ($Publisher.IndexOf(
        "REPLACE_WITH",
        [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw "Publisher still contains a placeholder."
}

$publishDirectory = Reset-RepositoryDirectory `
    -RepositoryRoot $repositoryRoot `
    -DirectoryPath $publishDirectory
$layoutDirectory = Reset-RepositoryDirectory `
    -RepositoryRoot $repositoryRoot `
    -DirectoryPath $layoutDirectory
$validationDirectory = Reset-RepositoryDirectory `
    -RepositoryRoot $repositoryRoot `
    -DirectoryPath $validationDirectory
New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null

$makeAppxPath = Resolve-WindowsSdkTool `
    -RepositoryRoot $repositoryRoot `
    -ToolName "makeappx.exe" `
    -BuildToolsVersion $WindowsSdkBuildToolsVersion `
    -Restore:(-not $SkipRestore)

if (-not $SkipRestore) {
    & $dotnetPath restore $projectPath `
        --runtime $RuntimeIdentifier `
        --configfile $nugetConfigPath `
        --packages $packagesPath `
        --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Runtime-specific restore failed."
    }
}

$selfContained = -not $FrameworkDependent
$selfContainedArgument = $selfContained.ToString().ToLowerInvariant()
& $dotnetPath publish $projectPath `
    --configuration $Configuration `
    --runtime $RuntimeIdentifier `
    --self-contained $selfContainedArgument `
    --no-restore `
    --output $publishDirectory `
    -p:RestorePackagesPath=$packagesPath `
    -p:AssemblyVersion=$PackageVersion `
    -p:FileVersion=$PackageVersion `
    -p:DebugSymbols=false `
    -p:DebugType=None
if ($LASTEXITCODE -ne 0) {
    throw "Application publish failed."
}

Get-ChildItem -LiteralPath $publishDirectory -Force |
    Copy-Item -Destination $layoutDirectory -Recurse -Force
Get-ChildItem -LiteralPath $layoutDirectory -Recurse -File -Filter "*.pdb" |
    Remove-Item -Force

$assetsDirectory = Join-Path $layoutDirectory "Assets"
New-Item -ItemType Directory -Path $assetsDirectory -Force | Out-Null
New-CapsuleLogo `
    -Path (Join-Path $assetsDirectory "StoreLogo.png") `
    -Width 50 `
    -Height 50
New-CapsuleLogo `
    -Path (Join-Path $assetsDirectory "Square44x44Logo.png") `
    -Width 44 `
    -Height 44
New-CapsuleLogo `
    -Path (Join-Path $assetsDirectory "Square150x150Logo.png") `
    -Width 150 `
    -Height 150
New-CapsuleLogo `
    -Path (Join-Path $assetsDirectory "Wide310x150Logo.png") `
    -Width 310 `
    -Height 150
New-CapsuleLogo `
    -Path (Join-Path $assetsDirectory "Square310x310Logo.png") `
    -Width 310 `
    -Height 310

$architecture = switch ($RuntimeIdentifier) {
    "win-x64" { "x64" }
    default { throw "Unsupported runtime identifier: $RuntimeIdentifier" }
}
$manifest = [IO.File]::ReadAllText($templatePath)
$manifest = $manifest.Replace(
    "{{IDENTITY_NAME}}",
    (ConvertTo-XmlAttributeValue $IdentityName))
$manifest = $manifest.Replace(
    "{{PUBLISHER}}",
    (ConvertTo-XmlAttributeValue $Publisher))
$manifest = $manifest.Replace(
    "{{VERSION}}",
    $PackageVersion)
$manifest = $manifest.Replace(
    "{{ARCHITECTURE}}",
    $architecture)
$manifest = $manifest.Replace(
    "{{PUBLISHER_DISPLAY_NAME}}",
    (ConvertTo-XmlAttributeValue $PublisherDisplayName))
if ($manifest.IndexOf(
        "{{",
        [StringComparison]::Ordinal) -ge 0) {
    throw "The package manifest still contains template tokens."
}

$utf8NoBom = New-Object Text.UTF8Encoding($false)
$manifestPath = Join-Path $layoutDirectory "AppxManifest.xml"
[IO.File]::WriteAllText($manifestPath, $manifest, $utf8NoBom)

$applicationFileNames = @(
    "WindowsDynamicCapsule.exe",
    "WindowsDynamicCapsule.dll")
$executablePayloads = @(
    Get-ChildItem -LiteralPath $layoutDirectory -Recurse -File |
        Where-Object { $_.Extension -in @(".exe", ".dll") }
)
$unsignedPayloads = @()
$invalidPayloads = @()
foreach ($payload in $executablePayloads) {
    $payloadSignature = Get-AuthenticodeSignature `
        -LiteralPath $payload.FullName
    if ($payloadSignature.Status -eq "NotSigned") {
        $unsignedPayloads += $payload
    }
    elseif ($payloadSignature.Status -ne "Valid") {
        $invalidPayloads += [PSCustomObject]@{
            Path = $payload.FullName
            Status = $payloadSignature.Status.ToString()
        }
    }
}

if ($invalidPayloads.Count -gt 0) {
    throw (
        "Published payload contains invalid signatures: " +
        (($invalidPayloads | ConvertTo-Json -Compress) -join ", "))
}

$unexpectedUnsignedPayloads = @(
    $unsignedPayloads |
        Where-Object { $_.Name -notin $applicationFileNames }
)
if ($unexpectedUnsignedPayloads.Count -gt 0) {
    throw (
        "Published payload contains unexpected unsigned binaries: " +
        (($unexpectedUnsignedPayloads.Name | Sort-Object) -join ", "))
}

$signToolPath = $null
if ($null -ne $certificate) {
    $signToolPath = Resolve-WindowsSdkTool `
        -RepositoryRoot $repositoryRoot `
        -ToolName "signtool.exe" `
        -BuildToolsVersion $WindowsSdkBuildToolsVersion

    foreach ($applicationFileName in $applicationFileNames) {
        $applicationFile = Join-Path $layoutDirectory $applicationFileName
        if (-not (Test-Path -LiteralPath $applicationFile -PathType Leaf)) {
            throw "Published application file is missing: $applicationFileName"
        }

        Invoke-CodeSign `
            -SignToolPath $signToolPath `
            -Thumbprint $certificate.Thumbprint `
            -FilePath $applicationFile
    }

    foreach ($applicationFileName in $applicationFileNames) {
        $applicationFile = Join-Path $layoutDirectory $applicationFileName
        Assert-CodeSignature `
            -FilePath $applicationFile `
            -ExpectedThumbprint $certificate.Thumbprint `
            -AllowUntrusted:$AllowUntrustedDevelopmentCertificate `
            -RequireTimestamp:(-not $SkipTimestamp)
    }
}

$packageSuffix = if ($null -eq $certificate) {
    ".unsigned.msix"
}
else {
    ".msix"
}
$packageName =
    "WindowsDynamicCapsule_{0}_{1}{2}" -f
    $PackageVersion,
    $architecture,
    $packageSuffix
$packagePath = Join-Path $artifactsRoot $packageName
if (Test-Path -LiteralPath $packagePath) {
    Remove-Item -LiteralPath $packagePath -Force
}

$packOutput = @(& $makeAppxPath pack `
    /d $layoutDirectory `
    /p $packagePath `
    /o `
    /h SHA256)
$packExitCode = $LASTEXITCODE
$packOutput | Select-Object -Last 8 | Out-Host
if ($packExitCode -ne 0) {
    throw "MakeAppx failed to create the package."
}

if ($null -ne $certificate) {
    Invoke-CodeSign `
        -SignToolPath $signToolPath `
        -Thumbprint $certificate.Thumbprint `
        -FilePath $packagePath
}

$unpackOutput = @(& $makeAppxPath unpack `
    /p $packagePath `
    /d $validationDirectory `
    /o)
$unpackExitCode = $LASTEXITCODE
$unpackOutput | Select-Object -Last 8 | Out-Host
if ($unpackExitCode -ne 0) {
    throw "MakeAppx could not unpack the generated package."
}

$validatedManifestPath = Join-Path $validationDirectory "AppxManifest.xml"
[xml] $validatedManifest = Get-Content `
    -LiteralPath $validatedManifestPath `
    -Raw `
    -Encoding utf8
if (($validatedManifest.Package.Identity.Name -ne $IdentityName) -or
    ($validatedManifest.Package.Identity.Publisher -ne $Publisher) -or
    ($validatedManifest.Package.Identity.Version -ne $PackageVersion) -or
    ($validatedManifest.Package.Identity.ProcessorArchitecture -ne
        $architecture)) {
    throw "Generated package identity does not match the requested values."
}

foreach ($requiredPath in @(
        "WindowsDynamicCapsule.exe",
        "WindowsDynamicCapsule.dll",
        "Assets\Square44x44Logo.png",
        "Assets\Square150x150Logo.png")) {
    if (-not (Test-Path `
            -LiteralPath (Join-Path $validationDirectory $requiredPath) `
            -PathType Leaf)) {
        throw "Generated package is missing $requiredPath."
    }
}

if ($null -ne $certificate) {
    if (-not $AllowUntrustedDevelopmentCertificate) {
        & $signToolPath verify /pa /all /v $packagePath
        if ($LASTEXITCODE -ne 0) {
            throw "The generated package signature could not be verified."
        }
    }

    Assert-CodeSignature `
        -FilePath $packagePath `
        -ExpectedThumbprint $certificate.Thumbprint `
        -AllowUntrusted:$AllowUntrustedDevelopmentCertificate `
        -RequireTimestamp:(-not $SkipTimestamp)
}

$packageHash = Get-FileHash -LiteralPath $packagePath -Algorithm SHA256
$metadata = [ordered]@{
    schemaVersion = 1
    package = $packagePath
    sha256 = $packageHash.Hash
    identityName = $IdentityName
    publisher = $Publisher
    publisherDisplayName = $PublisherDisplayName
    packageVersion = $PackageVersion
    architecture = $architecture
    runtimeIdentifier = $RuntimeIdentifier
    selfContained = $selfContained
    signed = $null -ne $certificate
    certificateThumbprint = if ($null -eq $certificate) {
        $null
    }
    else {
        $certificate.Thumbprint
    }
    certificateChainTrusted = $certificateChainTrusted
    timestamped = ($null -ne $certificate) -and (-not $SkipTimestamp)
    windowsSdkBuildToolsVersion = $WindowsSdkBuildToolsVersion
    executablePayloadCount = $executablePayloads.Count
    unsignedPayloadCountBeforeSigning = $unsignedPayloads.Count
    invalidPayloadSignatureCount = $invalidPayloads.Count
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
}
$metadataPath = "$packagePath.metadata.json"
[IO.File]::WriteAllText(
    $metadataPath,
    ($metadata | ConvertTo-Json -Depth 4),
    $utf8NoBom)

[PSCustomObject]@{
    Package = $packagePath
    Metadata = $metadataPath
    Sha256 = $packageHash.Hash
    Signed = $null -ne $certificate
    Publisher = $Publisher
    Version = $PackageVersion
    Architecture = $architecture
    SelfContained = $selfContained
    ValidationDirectory = $validationDirectory
}
