<#
.SYNOPSIS
Runs data-only checks for Dynamic Capsule source and privacy policy rules.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$failures = New-Object System.Collections.Generic.List[string]

function Assert-Policy {
    param(
        [bool] $Condition,
        [string] $Message
    )

    if (-not $Condition) {
        $failures.Add($Message)
    }
}

function Test-SourceMatch {
    param(
        [pscustomobject] $Event,
        [string[]] $Sources
    )

    foreach ($source in $Sources) {
        if ($source.Equals(
                $Event.SourceId,
                [StringComparison]::OrdinalIgnoreCase) -or
            $source.Equals(
                $Event.Source,
                [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Test-EventAllowed {
    param(
        [pscustomobject] $Event,
        [string[]] $AllowList,
        [string[]] $BlockList
    )

    if ($Event.Kind -ne "notification") {
        return $true
    }

    if (Test-SourceMatch -Event $Event -Sources $BlockList) {
        return $false
    }

    return $AllowList.Count -eq 0 -or
        (Test-SourceMatch -Event $Event -Sources $AllowList)
}

function Protect-Event {
    param(
        [pscustomobject] $Event,
        [ValidateSet("full", "summary", "masked", "iconOnly")]
        [string] $PrivacyLevel
    )

    $result = [ordered]@{
        Title = $Event.Title
        Message = $Event.Message
        Progress = $Event.Progress
    }

    switch ($PrivacyLevel) {
        "summary" {
            if ($result.Message.Length -gt 72) {
                $result.Message = $result.Message.Substring(0, 71).TrimEnd() + "..."
            }
        }
        "masked" {
            $result.Title = if ($Event.Kind -eq "notification") {
                "notification content hidden"
            }
            else {
                "task content hidden"
            }
            $result.Message = "content hidden"
        }
        "iconOnly" {
            $result.Title = ""
            $result.Message = ""
            $result.Progress = $null
        }
    }

    return [PSCustomObject] $result
}

$notification = [PSCustomObject]@{
    Kind = "notification"
    SourceId = "app.id"
    Source = "Mail"
    Title = "Sensitive title"
    Message = ("x" * 100)
    Progress = $null
}
$task = [PSCustomObject]@{
    Kind = "task"
    SourceId = "codex"
    Source = "Codex"
    Title = "Build"
    Message = "Compiling"
    Progress = 0.5
}

Assert-Policy `
    -Condition (-not (Test-EventAllowed `
        -Event $notification `
        -AllowList @() `
        -BlockList @("app.id"))) `
    -Message "Block list must reject a matching notification."
Assert-Policy `
    -Condition (-not (Test-EventAllowed `
        -Event $notification `
        -AllowList @("another.app") `
        -BlockList @())) `
    -Message "A non-empty allow list must reject unmatched notifications."
Assert-Policy `
    -Condition (Test-EventAllowed `
        -Event $notification `
        -AllowList @("Mail") `
        -BlockList @()) `
    -Message "Allow list must match a visible source name."
Assert-Policy `
    -Condition (Test-EventAllowed `
        -Event $task `
        -AllowList @() `
        -BlockList @("codex")) `
    -Message "Notification source rules must not block tasks."

$full = Protect-Event -Event $notification -PrivacyLevel "full"
$summary = Protect-Event -Event $notification -PrivacyLevel "summary"
$masked = Protect-Event -Event $notification -PrivacyLevel "masked"
$iconOnly = Protect-Event -Event $task -PrivacyLevel "iconOnly"

Assert-Policy `
    -Condition ($full.Message.Length -eq 100) `
    -Message "Full privacy must preserve the message."
Assert-Policy `
    -Condition ($summary.Message.Length -le 74) `
    -Message "Summary privacy must truncate long text."
Assert-Policy `
    -Condition ($masked.Title -eq "notification content hidden" -and
        $masked.Message -eq "content hidden") `
    -Message "Masked privacy must replace title and body."
Assert-Policy `
    -Condition ($iconOnly.Title.Length -eq 0 -and
        $iconOnly.Message.Length -eq 0 -and
        $null -eq $iconOnly.Progress) `
    -Message "Icon-only privacy must remove details and progress."

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

[PSCustomObject]@{
    Passed = $true
    SourceRuleChecks = 4
    PrivacyLevelChecks = 4
}
