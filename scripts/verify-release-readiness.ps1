[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot

function Read-RepositoryText {
    param([Parameter(Mandatory = $true)][string] $RelativePath)

    return Get-Content `
        -LiteralPath (Join-Path $repositoryRoot $RelativePath) `
        -Raw `
        -Encoding utf8
}

function Test-AllTextsContain {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Texts,

        [Parameter(Mandatory = $true)]
        [string[]] $RequiredValues
    )

    foreach ($text in $Texts) {
        foreach ($requiredValue in $RequiredValues) {
            if ($text.IndexOf(
                    $requiredValue,
                    [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                return $false
            }
        }
    }

    return $true
}

$manifest = Read-RepositoryText "packaging\Package.appxmanifest.template"
$storeSubmission = Read-RepositoryText "packaging\STORE_SUBMISSION.md"
$privacyDocuments = @(
    Read-RepositoryText "PRIVACY.md"
    Read-RepositoryText "PRIVACY.en.md"
    Read-RepositoryText "PRIVACY.zh-TW.md"
)
$connectivitySource = Read-RepositoryText (
    "src\DynamicCapsule\Services\ConnectivityEventService.cs")
$lyricsSource = Read-RepositoryText (
    "src\DynamicCapsule\Services\LyricsService.cs")

$sourceFiles = @(
    Get-ChildItem `
        -LiteralPath (Join-Path $repositoryRoot "src\DynamicCapsule") `
        -Recurse `
        -File `
        -Include *.cs,*.xaml |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
)
$sourceText = ($sourceFiles | ForEach-Object {
        Get-Content -LiteralPath $_.FullName -Raw -Encoding utf8
    }) -join "`n"

$capabilityMatches = [regex]::Matches(
    $manifest,
    '<(?:(?:uap3|rescap):Capability|DeviceCapability)\s+Name="([^"]+)"')
$manifestCapabilities = @(
    $capabilityMatches |
        ForEach-Object { $_.Groups[1].Value } |
        Sort-Object -Unique
)
$approvedCapabilities = @(
    "bluetooth"
    "runFullTrust"
    "userNotificationListener"
)
$capabilitiesMatch =
    ($manifestCapabilities -join "|") -eq
    ($approvedCapabilities -join "|")

$repositoryPaths = @(
    & git -c "safe.directory=$repositoryRoot" `
        ls-files --cached --others --exclude-standard
)
if ($LASTEXITCODE -ne 0) {
    throw "Unable to enumerate tracked repository files."
}

$forbiddenTrackedExtensions = @(
    ".appx"
    ".appxbundle"
    ".key"
    ".msix"
    ".msixbundle"
    ".p12"
    ".pem"
    ".pfx"
    ".pvk"
)
$trackedBinaryLeak = @($repositoryPaths | Where-Object {
        $forbiddenTrackedExtensions -contains
            [IO.Path]::GetExtension($_).ToLowerInvariant()
    }).Count -gt 0

$textExtensions = @(
    ".config"
    ".cs"
    ".csproj"
    ".gitignore"
    ".json"
    ".md"
    ".props"
    ".ps1"
    ".slnx"
    ".targets"
    ".xaml"
    ".xml"
    ".yaml"
    ".yml"
)
$trackedText = New-Object Text.StringBuilder
foreach ($repositoryPath in $repositoryPaths) {
    $extension = [IO.Path]::GetExtension($repositoryPath).ToLowerInvariant()
    if ($textExtensions -notcontains $extension) {
        continue
    }

    $fullPath = Join-Path $repositoryRoot $repositoryPath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        continue
    }

    [void] $trackedText.AppendLine($repositoryPath)
    [void] $trackedText.AppendLine(
        (Get-Content -LiteralPath $fullPath -Raw -Encoding utf8))
}
$trackedTextValue = $trackedText.ToString()

$containsPrivateMaterial =
    $trackedTextValue -match '(?i)[A-Z]:\\Users\\[^\r\n]+' -or
    $trackedTextValue -match '(?i)[A-Z]:\\Project Dynamic Island' -or
    $trackedTextValue -match '(?i)gh[opusr]_[A-Za-z0-9_]{20,}' -or
    $trackedTextValue -match
        '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----' -or
    $trackedTextValue -match
        '(?i)CN=[0-9A-F]{8}(?:-[0-9A-F]{4}){3}-[0-9A-F]{12}'

$privacyTerms = @(
    "Bluetooth"
    "Wi-Fi"
    "SSID"
    "Windows Toast"
    "Named Pipe"
    "settings.json"
    "lyrics-cache"
    "LRCLIB"
    "c.y.qq.com"
    "y.gtimg.cn"
)
$privacyAligned = Test-AllTextsContain `
    -Texts $privacyDocuments `
    -RequiredValues $privacyTerms

$declaredNetworkHosts = @(
    "lrclib.net"
    "c.y.qq.com"
    "y.gtimg.cn"
)
$networkHostsAligned =
    (Test-AllTextsContain `
        -Texts $privacyDocuments `
        -RequiredValues $declaredNetworkHosts) -and
    (Test-AllTextsContain `
        -Texts @($lyricsSource) `
        -RequiredValues $declaredNetworkHosts)

$checks = @(
    [PSCustomObject]@{
        Name = "Manifest contains only reviewed capabilities"
        Passed = $capabilitiesMatch
    },
    [PSCustomObject]@{
        Name = "Three privacy policies disclose connectivity data"
        Passed =
            $privacyAligned -and
            $connectivitySource.Contains("GetConnectedSsid") -and
            $connectivitySource.Contains("System.ItemNameDisplay")
    },
    [PSCustomObject]@{
        Name = "Lyrics network hosts match privacy disclosures"
        Passed = $networkHostsAligned
    },
    [PSCustomObject]@{
        Name = "Store submission explains every sensitive capability"
        Passed =
            $storeSubmission.Contains("### runFullTrust") -and
            $storeSubmission.Contains("### userNotificationListener") -and
            $storeSubmission.Contains("### bluetooth") -and
            $storeSubmission.Contains("Wi-Fi")
    },
    [PSCustomObject]@{
        Name = "Local task transport remains current-user only"
        Passed =
            $sourceText.Contains("NamedPipeServerStream") -and
            $sourceText.Contains("PipeOptions.CurrentUserOnly") -and
            $sourceText -notmatch
                '\b(?:TcpListener|HttpListener|UdpClient)\b'
    },
    [PSCustomObject]@{
        Name = "Application source contains no telemetry SDK"
        Passed = $sourceText -notmatch
            '\b(?:TelemetryClient|ApplicationInsights|OpenTelemetry|SentrySdk)\b'
    },
    [PSCustomObject]@{
        Name = "Repository files contain no private release material"
        Passed = (-not $trackedBinaryLeak) -and (-not $containsPrivateMaterial)
    },
    [PSCustomObject]@{
        Name = "Application source contains no debug output hooks"
        Passed = $sourceText -notmatch
            '\b(?:Debug|Trace)\.Write(?:Line)?\s*\('
    }
)

$failedChecks = @($checks | Where-Object { -not $_.Passed })
$checks | Format-Table -AutoSize
if ($failedChecks.Count -gt 0) {
    throw "$($failedChecks.Count) release readiness check(s) failed."
}

[PSCustomObject]@{
    Passed = $true
    CheckCount = $checks.Count
    PrivacyDocumentCount = $privacyDocuments.Count
    ManifestCapabilityCount = $manifestCapabilities.Count
    AuditedFileCount = $repositoryPaths.Count
    FilesModified = $false
}
