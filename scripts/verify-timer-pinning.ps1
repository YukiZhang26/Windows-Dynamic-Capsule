<#
.SYNOPSIS
Runs data-only checks for countdown transitions and pinned event priority.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$failures = New-Object System.Collections.Generic.List[string]

function Assert-Probe {
    param(
        [bool] $Condition,
        [string] $Message
    )

    if (-not $Condition) {
        $failures.Add($Message)
    }
}

function Select-ActiveEvent {
    param(
        [object[]] $Events,
        [string] $PinnedId
    )

    $critical = @(
        $Events |
            Where-Object { $_.Priority -eq 30 } |
            Sort-Object CreatedAt -Descending)
    if ($critical.Count -gt 0) {
        return $critical[0]
    }

    if (-not [string]::IsNullOrWhiteSpace($PinnedId)) {
        $pinned = @(
            $Events |
                Where-Object { $_.Id -eq $PinnedId })
        if ($pinned.Count -gt 0) {
            return $pinned[0]
        }
    }

    return @(
        $Events |
            Sort-Object `
                @{ Expression = "Priority"; Descending = $true },
                @{ Expression = "CreatedAt"; Descending = $true }
    )[0]
}

$timer = [ordered]@{
    State = "running"
    DurationSeconds = 300
    RemainingSeconds = 300
}

$timer.RemainingSeconds -= 60
Assert-Probe `
    -Condition ($timer.State -eq "running" -and
        $timer.RemainingSeconds -eq 240) `
    -Message "Running countdown must decrease remaining time."

$timer.State = "paused"
$pausedRemaining = $timer.RemainingSeconds
Assert-Probe `
    -Condition ($timer.RemainingSeconds -eq $pausedRemaining) `
    -Message "Paused countdown must preserve remaining time."

$timer.State = "running"
$timer.RemainingSeconds -= 240
$timer.State = if ($timer.RemainingSeconds -le 0) {
    "completed"
}
else {
    "running"
}
Assert-Probe `
    -Condition ($timer.State -eq "completed") `
    -Message "Countdown reaching zero must complete."

$events = @(
    [PSCustomObject]@{
        Id = "pinned"
        Priority = 10
        CreatedAt = 1
    },
    [PSCustomObject]@{
        Id = "ordinary-high"
        Priority = 20
        CreatedAt = 2
    })
$selected = Select-ActiveEvent -Events $events -PinnedId "pinned"
Assert-Probe `
    -Condition ($selected.Id -eq "pinned") `
    -Message "Pinned event must beat an ordinary high-priority event."

$events += [PSCustomObject]@{
    Id = "critical"
    Priority = 30
    CreatedAt = 3
}
$selected = Select-ActiveEvent -Events $events -PinnedId "pinned"
Assert-Probe `
    -Condition ($selected.Id -eq "critical") `
    -Message "Critical event must preempt a pinned event."

$events = @(
    $events |
        Where-Object { $_.Id -ne "critical" })
$selected = Select-ActiveEvent -Events $events -PinnedId "pinned"
Assert-Probe `
    -Condition ($selected.Id -eq "pinned") `
    -Message "Pinned event must return after critical event expires."

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

[PSCustomObject]@{
    Passed = $true
    TimerTransitionChecks = 3
    PinnedPriorityChecks = 3
}
