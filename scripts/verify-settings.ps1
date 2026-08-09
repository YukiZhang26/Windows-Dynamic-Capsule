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
        Join-Path $env:LOCALAPPDATA "WindowsDynamicCapsule\settings.json")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resolvedPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath(
    $Path)

function New-Result {
    param(
        [bool] $Valid,
        [bool] $WouldUseDefaults,
        [string] $Reason
    )

    [PSCustomObject]@{
        Path = $resolvedPath
        Valid = $Valid
        WouldUseDefaults = $WouldUseDefaults
        Reason = $Reason
    }
}

if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
    New-Result -Valid $true -WouldUseDefaults $true `
        -Reason "Settings file does not exist; defaults will be used."
    return
}

$file = Get-Item -LiteralPath $resolvedPath
if ($file.Length -le 0 -or $file.Length -gt (64 * 1024)) {
    New-Result -Valid $false -WouldUseDefaults $true `
        -Reason "Settings file is empty or exceeds 64 KB."
    return
}

try {
    $settings = Get-Content -LiteralPath $resolvedPath -Raw -Encoding UTF8 |
        ConvertFrom-Json
}
catch {
    New-Result -Valid $false -WouldUseDefaults $true `
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
    New-Result -Valid $false -WouldUseDefaults $true `
        -Reason "Missing properties: $($missing -join ', ')"
    return
}

if ($settings.schemaVersion -ne 3) {
    New-Result -Valid $false -WouldUseDefaults $true `
        -Reason "Unsupported schema version: $($settings.schemaVersion)."
    return
}

if ($settings.monitorTarget -notin @(
        "followActiveWindow",
        "primaryDisplay")) {
    New-Result -Valid $false -WouldUseDefaults $true `
        -Reason "monitorTarget is invalid."
    return
}

$topGap = 0.0
if (-not [double]::TryParse(
        [string] $settings.topGap,
        [ref] $topGap)) {
    New-Result -Valid $false -WouldUseDefaults $true `
        -Reason "topGap is not numeric."
    return
}

if ($settings.enableAnimations -isnot [bool] -or
    $settings.hideInFullscreen -isnot [bool] -or
    $settings.doNotDisturb -isnot [bool] -or
    $settings.startWithWindows -isnot [bool]) {
    New-Result -Valid $false -WouldUseDefaults $true `
        -Reason "Boolean settings are invalid."
    return
}

if ($settings.privacyLevel -notin @(
        "full",
        "summary",
        "masked",
        "iconOnly")) {
    New-Result -Valid $false -WouldUseDefaults $true `
        -Reason "privacyLevel is invalid."
    return
}

$lyricsFallbackProperty =
    $settings.PSObject.Properties["lyricsFallbackProvider"]
if ($null -ne $lyricsFallbackProperty -and
    $lyricsFallbackProperty.Value -notin @("none", "qqMusic")) {
    New-Result -Valid $false -WouldUseDefaults $true `
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
    New-Result -Valid $false -WouldUseDefaults $true `
        -Reason "Notification source lists must be arrays."
    return
}

$normalizationNote = if ($topGap -lt 0 -or $topGap -gt 48) {
    "Valid; topGap will be clamped to 0-48 DIP."
}
else {
    "Settings file is valid."
}

New-Result -Valid $true -WouldUseDefaults $false -Reason $normalizationNote
