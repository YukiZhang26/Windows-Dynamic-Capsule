[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$singleInstancePath = Join-Path $repositoryRoot (
    "src\DynamicCapsule\Services\SingleInstanceService.cs")
$startupRegistrationPath = Join-Path $repositoryRoot (
    "src\DynamicCapsule\Services\StartupRegistrationService.cs")
$settingsPath = Join-Path $repositoryRoot (
    "src\DynamicCapsule\Models\AppSettings.cs")
$startScriptPath = Join-Path $repositoryRoot (
    "scripts\start-dynamic-capsule.ps1")
$manifestTemplatePath = Join-Path $repositoryRoot (
    "packaging\Package.appxmanifest.template")

$singleInstanceSource = Get-Content `
    -LiteralPath $singleInstancePath `
    -Raw `
    -Encoding utf8
$startupRegistrationSource = Get-Content `
    -LiteralPath $startupRegistrationPath `
    -Raw `
    -Encoding utf8
$settingsSource = Get-Content `
    -LiteralPath $settingsPath `
    -Raw `
    -Encoding utf8
$startScriptSource = Get-Content `
    -LiteralPath $startScriptPath `
    -Raw `
    -Encoding utf8
$manifestTemplateSource = Get-Content `
    -LiteralPath $manifestTemplatePath `
    -Raw `
    -Encoding utf8

$checks = @(
    [PSCustomObject]@{
        Name = "Named pipe is current-user only"
        Passed = $singleInstanceSource.Contains(
            "PipeOptions.CurrentUserOnly")
    },
    [PSCustomObject]@{
        Name = "Secondary instance avoids UI context deadlock"
        Passed = $singleInstanceSource.Contains(
            "ConfigureAwait(false)")
    },
    [PSCustomObject]@{
        Name = "Forwarded commands have size and time limits"
        Passed =
            $singleInstanceSource.Contains("MaximumCommandCharacters") -and
            $singleInstanceSource.Contains("AddMinutes(-5)")
    },
    [PSCustomObject]@{
        Name = "Startup registration is current-user only"
        Passed =
            $startupRegistrationSource.Contains("Registry.CurrentUser") -and
            -not $startupRegistrationSource.Contains(
                "Registry.LocalMachine")
    },
    [PSCustomObject]@{
        Name = "Startup command uses hidden PowerShell"
        Passed =
            $startupRegistrationSource.Contains("-WindowStyle Hidden") -and
            $startupRegistrationSource.Contains("-NoProfile")
    },
    [PSCustomObject]@{
        Name = "Packaged app uses Windows StartupTask"
        Passed =
            $startupRegistrationSource.Contains(
                "StartupTask.GetAsync(StartupTaskId)") -and
            $startupRegistrationSource.Contains(
                "TrySetPackagedEnabledAsync") -and
            $manifestTemplateSource.Contains(
                'Category="windows.startupTask"') -and
            $manifestTemplateSource.Contains(
                'TaskId="DynamicCapsuleStartup"')
    },
    [PSCustomObject]@{
        Name = "Settings schema includes startup preference"
        Passed =
            $settingsSource.Contains("CurrentSchemaVersion = 3") -and
            $settingsSource.Contains("StartWithWindows")
    },
    [PSCustomObject]@{
        Name = "Launcher reports argument forwarding"
        Passed =
            $startScriptSource.Contains("Forwarded = `$true") -and
            $startScriptSource.Contains("--timer-minutes")
    }
)

$failedChecks = @($checks | Where-Object { -not $_.Passed })
$checks | Format-Table -AutoSize

if ($failedChecks.Count -gt 0) {
    throw "$($failedChecks.Count) startup lifecycle check(s) failed."
}

[PSCustomObject]@{
    Passed = $true
    CheckCount = $checks.Count
    RegistryModified = $false
}
