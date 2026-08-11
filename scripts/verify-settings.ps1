<#
.SYNOPSIS
Validates a Windows Dynamic Capsule settings file without changing it.

.EXAMPLE
.\scripts\verify-settings.ps1

.EXAMPLE
.\scripts\verify-settings.ps1 -Path .\tests\fixtures\settings\corrupt.json
#>

[CmdletBinding()]
param(
    [string] $Path = (
        Join-Path $env:LOCALAPPDATA "WindowsDynamicCapsule\settings.json"),

    [ValidateSet("Any", "Valid", "Defaults", "Invalid", "Unverified")]
    [string] $ExpectedOutcome = "Any"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resolvedPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath(
    $Path)

function New-Result {
    param(
        [bool] $Verified,
        [bool] $Valid,
        [bool] $WouldUseDefaults,
        [string] $Reason
    )

    $outcome = if (-not $Verified) {
        "Unverified"
    }
    elseif ($WouldUseDefaults) {
        "Defaults"
    }
    elseif ($Valid) {
        "Valid"
    }
    else {
        "Invalid"
    }

    $result = [PSCustomObject]@{
        Path = $resolvedPath
        Verified = $Verified
        Valid = $Valid
        WouldUseDefaults = $WouldUseDefaults
        Outcome = $outcome
        Reason = $Reason
    }

    if ($ExpectedOutcome -ne "Any" -and
        $outcome -ne $ExpectedOutcome) {
        throw "Expected settings outcome '$ExpectedOutcome', got '$outcome': $Reason"
    }

    $result
}

if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
    New-Result -Verified $true -Valid $true -WouldUseDefaults $true `
        -Reason "Settings file does not exist; defaults will be used."
    return
}

try {
    $file = Get-Item -LiteralPath $resolvedPath
}
catch {
    New-Result -Verified $false -Valid $false -WouldUseDefaults $false `
        -Reason "Settings file metadata could not be read: $($_.Exception.Message)"
    return
}

if ($file.Length -le 0 -or $file.Length -gt (64 * 1024)) {
    New-Result -Verified $true -Valid $false -WouldUseDefaults $true `
        -Reason "Settings file is empty or exceeds 64 KB."
    return
}

try {
    $settingsJson = Get-Content `
        -LiteralPath $resolvedPath `
        -Raw `
        -Encoding UTF8
}
catch {
    New-Result -Verified $false -Valid $false -WouldUseDefaults $false `
        -Reason "Settings file could not be read: $($_.Exception.Message)"
    return
}

try {
    $settings = $settingsJson | ConvertFrom-Json
}
catch {
    New-Result -Verified $true -Valid $false -WouldUseDefaults $true `
        -Reason "JSON could not be parsed: $($_.Exception.Message)"
    return
}

$requiredProperties = @(
    "schemaVersion",
    "monitorTarget",
    "topGap",
    "enableAnimations",
    "hideInFullscreen",
    "doNotDisturb",
    "startWithWindows",
    "privacyLevel",
    "notificationAllowList",
    "notificationBlockList")
$propertyNames = @($settings.PSObject.Properties.Name)
$missing = @(
    $requiredProperties |
        Where-Object { $_ -notin $propertyNames })
if ($missing.Count -gt 0) {
    New-Result -Verified $true -Valid $false -WouldUseDefaults $true `
        -Reason "Missing properties: $($missing -join ', ')"
    return
}

if ($settings.schemaVersion -ne 3) {
    New-Result -Verified $true -Valid $false -WouldUseDefaults $true `
        -Reason "Unsupported schema version: $($settings.schemaVersion)."
    return
}

if ($settings.monitorTarget -notin @(
        "followActiveWindow",
        "primaryDisplay")) {
    New-Result -Verified $true -Valid $false -WouldUseDefaults $true `
        -Reason "monitorTarget is invalid."
    return
}

$topGap = 0.0
if (-not [double]::TryParse(
        [string] $settings.topGap,
        [ref] $topGap)) {
    New-Result -Verified $true -Valid $false -WouldUseDefaults $true `
        -Reason "topGap is not numeric."
    return
}

if ($settings.enableAnimations -isnot [bool] -or
    $settings.hideInFullscreen -isnot [bool] -or
    $settings.doNotDisturb -isnot [bool] -or
    $settings.startWithWindows -isnot [bool]) {
    New-Result -Verified $true -Valid $false -WouldUseDefaults $true `
        -Reason "Boolean settings are invalid."
    return
}

if ($settings.privacyLevel -notin @(
        "full",
        "summary",
        "masked",
        "iconOnly")) {
    New-Result -Verified $true -Valid $false -WouldUseDefaults $true `
        -Reason "privacyLevel is invalid."
    return
}

$lyricsFallbackProperty =
    $settings.PSObject.Properties["lyricsFallbackProvider"]
if ($null -ne $lyricsFallbackProperty -and
    $lyricsFallbackProperty.Value -notin @("none", "qqMusic")) {
    New-Result -Verified $true -Valid $false -WouldUseDefaults $true `
        -Reason "lyricsFallbackProvider is invalid."
    return
}

$allowListIsValid =
    $null -ne $settings.notificationAllowList -and
    $settings.notificationAllowList -is [array]
$blockListIsValid =
    $null -ne $settings.notificationBlockList -and
    $settings.notificationBlockList -is [array]
if (-not $allowListIsValid -or -not $blockListIsValid) {
    New-Result -Verified $true -Valid $false -WouldUseDefaults $true `
        -Reason "Notification source lists must be arrays."
    return
}

$normalizationNote = if ($topGap -lt 0 -or $topGap -gt 48) {
    "Valid; topGap will be clamped to 0-48 DIP."
}
else {
    "Settings file is valid."
}

New-Result -Verified $true -Valid $true -WouldUseDefaults $false `
    -Reason $normalizationNote
