<#
.SYNOPSIS
Starts Windows Dynamic Capsule with the repository-local .NET runtime.
#>

[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Debug",

    [ValidateRange(1, 1440)]
    [int] $TimerMinutes,

    [switch] $ShowSettings,

    [switch] $ShowDiagnostics
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$runtimeRoot = Join-Path $repositoryRoot ".dotnet"
$executablePath = Join-Path $repositoryRoot (
    "src\DynamicCapsule\bin\$Configuration\" +
    "net10.0-windows10.0.19041.0\WindowsDynamicCapsule.exe")

if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "Build output was not found: $executablePath"
}

$existing = Get-Process -Name "WindowsDynamicCapsule" -ErrorAction SilentlyContinue
$hadExistingInstance = $null -ne $existing

$previousRuntimeRoot =
    [Environment]::GetEnvironmentVariable("DOTNET_ROOT", "Process")

try {
    [Environment]::SetEnvironmentVariable(
        "DOTNET_ROOT",
        $runtimeRoot,
        "Process")
    $argumentList = @()
    if ($PSBoundParameters.ContainsKey("TimerMinutes")) {
        $argumentList += @("--timer-minutes", $TimerMinutes)
    }
    if ($ShowSettings) {
        $argumentList += "--show-settings"
    }
    if ($ShowDiagnostics) {
        $argumentList += "--show-diagnostics"
    }

    $startParameters = @{
        FilePath = $executablePath
        WorkingDirectory = Split-Path -Parent $executablePath
        PassThru = $true
    }
    if ($argumentList.Count -gt 0) {
        $startParameters.ArgumentList = $argumentList
    }

    $process = Start-Process @startParameters
}
finally {
    [Environment]::SetEnvironmentVariable(
        "DOTNET_ROOT",
        $previousRuntimeRoot,
        "Process")
}

Start-Sleep -Milliseconds 1500
if ($process.HasExited) {
    if ($hadExistingInstance -and $process.ExitCode -eq 0) {
        [PSCustomObject]@{
            Started = $false
            Forwarded = $true
            TargetProcessIds = @($existing.Id)
            Configuration = $Configuration
            Arguments = $argumentList
        }
        return
    }

    $unsignedExitCode = [BitConverter]::ToUInt32(
        [BitConverter]::GetBytes([int32] $process.ExitCode),
        0)
    throw (
        "Dynamic Capsule exited during startup. " +
        "Exit code: 0x{0:X8}" -f $unsignedExitCode)
}

[PSCustomObject]@{
    Started = $true
    Forwarded = $false
    ProcessId = $process.Id
    Configuration = $Configuration
    Executable = $executablePath
    Arguments = $argumentList
}
