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

$serviceSource = Read-Source (
    "src\DynamicCapsule\Services\AccessibilityPreferencesService.cs")
$appXaml = Read-Source "src\DynamicCapsule\App.xaml"
$appSource = Read-Source "src\DynamicCapsule\App.xaml.cs"
$capsuleSource = Read-Source "src\DynamicCapsule\CapsuleWindow.xaml.cs"
$capsuleXaml = Read-Source "src\DynamicCapsule\CapsuleWindow.xaml"
$settingsXaml = Read-Source "src\DynamicCapsule\SettingsWindow.xaml"
$timerXaml = Read-Source "src\DynamicCapsule\TimerWindow.xaml"
$diagnosticsXaml = Read-Source "src\DynamicCapsule\DiagnosticsWindow.xaml"
$traySource = Read-Source "src\DynamicCapsule\Services\TrayService.cs"

$dialogXaml = $settingsXaml + $timerXaml + $diagnosticsXaml
$checks = @(
    [PSCustomObject]@{
        Name = "System accessibility preferences are observed"
        Passed =
            $serviceSource.Contains("SystemParameters.HighContrast") -and
            $serviceSource.Contains(
                "SystemParameters.ClientAreaAnimation") -and
            $serviceSource.Contains("StaticPropertyChanged")
    },
    [PSCustomObject]@{
        Name = "Application theme updates at runtime"
        Passed =
            $appSource.Contains("ApplyTheme(Resources)") -and
            $appSource.Contains("OnAccessibilityPreferencesChanged")
    },
    [PSCustomObject]@{
        Name = "System reduce-motion overrides capsule animations"
        Passed =
            $capsuleSource.Contains("ShouldAnimate") -and
            $capsuleSource.Contains(
                "!_accessibilityPreferences.ReduceMotion") -and
            $capsuleSource.Contains(
                "StopAnimationsAndApplyCurrentLayout")
    },
    [PSCustomObject]@{
        Name = "Capsule uses dynamic high-contrast resources"
        Passed =
            $capsuleXaml.Contains(
                "{DynamicResource CompactCapsuleSurfaceBrush}") -and
            $appXaml.Contains(
                'x:Key="CompactCapsuleSurfaceBrush"') -and
            $serviceSource.Contains(
                'resources["CompactCapsuleSurfaceBrush"]') -and
            $capsuleXaml.Contains(
                "{DynamicResource PrimaryTextBrush}") -and
            $capsuleXaml.Contains(
                "{DynamicResource ProgressBrush}")
    },
    [PSCustomObject]@{
        Name = "Dialogs use cyclic keyboard navigation"
        Passed =
            ([regex]::Matches(
                $dialogXaml,
                'KeyboardNavigation.TabNavigation="Cycle"').Count -eq 3)
    },
    [PSCustomObject]@{
        Name = "Dialogs expose default and cancel actions"
        Passed =
            $settingsXaml.Contains('IsDefault="True"') -and
            $settingsXaml.Contains('IsCancel="True"') -and
            $timerXaml.Contains('IsDefault="True"') -and
            $timerXaml.Contains('IsCancel="True"') -and
            $diagnosticsXaml.Contains('IsCancel="True"')
    },
    [PSCustomObject]@{
        Name = "Validation and copy status are announced"
        Passed =
            $timerXaml.Contains(
                'AutomationProperties.LiveSetting="Assertive"') -and
            $diagnosticsXaml.Contains(
                'AutomationProperties.LiveSetting="Polite"')
    },
    [PSCustomObject]@{
        Name = "Tray provides keyboard-accessible capsule control"
        Passed =
            $traySource.Contains("CapsuleToggleRequested") -and
            $traySource.Contains("OnToggleCapsuleItemClick") -and
            $traySource.Contains("(&O)")
    }
)

$failedChecks = @($checks | Where-Object { -not $_.Passed })
$checks | Format-Table -AutoSize

if ($failedChecks.Count -gt 0) {
    throw "$($failedChecks.Count) accessibility check(s) failed."
}

Add-Type -AssemblyName PresentationFramework
[PSCustomObject]@{
    Passed = $true
    CheckCount = $checks.Count
    SystemHighContrast = [System.Windows.SystemParameters]::HighContrast
    SystemReduceMotion =
        -not [System.Windows.SystemParameters]::ClientAreaAnimation
    SystemSettingsModified = $false
}
