<#
.SYNOPSIS
Sends one local task event to Windows Dynamic Capsule.

.EXAMPLE
.\scripts\send-task-event.ps1 -Type task.started -Id build-42 `
    -Source codex -Title "正在构建项目" -Message "准备编译"

.EXAMPLE
.\scripts\send-task-event.ps1 -Type task.progress -Id build-42 `
    -Source codex -Title "正在构建项目" -Message "编译主程序" -Progress 0.65

.EXAMPLE
.\scripts\send-task-event.ps1 -Type task.completed -Id build-42 `
    -Source codex -Title "构建完成" -Message "没有发现错误"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet(
        "task.started",
        "task.progress",
        "task.completed",
        "task.failed",
        "task.cancelled")]
    [string] $Type,

    [Parameter(Mandatory)]
    [ValidatePattern("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$")]
    [string] $Id,

    [ValidatePattern("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$")]
    [string] $Source = "codex",

    [Parameter(Mandatory)]
    [ValidateLength(1, 120)]
    [string] $Title,

    [ValidateLength(0, 300)]
    [string] $Message = "",

    [ValidateRange(0, 1)]
    [double] $Progress,

    [ValidateSet("low", "normal", "high", "critical")]
    [string] $Priority,

    [ValidateRange(100, 30000)]
    [int] $ConnectTimeoutMs = 1000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Title)) {
    throw "Title cannot be empty or whitespace."
}

$payload = [ordered]@{
    version = 1
    type = $Type
    id = $Id
    source = $Source
    title = $Title.Trim()
    message = $Message.Trim()
    timestamp = [DateTimeOffset]::UtcNow.ToString("O")
}

if ($PSBoundParameters.ContainsKey("Progress")) {
    $payload.progress = $Progress
}

if ($PSBoundParameters.ContainsKey("Priority")) {
    $payload.priority = $Priority
}

$json = $payload | ConvertTo-Json -Compress
$encoding = [Text.UTF8Encoding]::new($false)
if ($encoding.GetByteCount($json) -gt (16 * 1024)) {
    throw "The encoded task event exceeds the 16 KB limit."
}

$pipe = [IO.Pipes.NamedPipeClientStream]::new(
    ".",
    "DynamicCapsule.v1",
    [IO.Pipes.PipeDirection]::Out,
    [IO.Pipes.PipeOptions]::Asynchronous)

try {
    $pipe.Connect($ConnectTimeoutMs)
    $writer = [IO.StreamWriter]::new($pipe, $encoding, 1024, $true)
    try {
        $writer.WriteLine($json)
        $writer.Flush()
    }
    finally {
        $writer.Dispose()
    }
}
catch [TimeoutException] {
    throw "Dynamic Capsule is not listening on pipe 'DynamicCapsule.v1'."
}
finally {
    $pipe.Dispose()
}

[PSCustomObject]@{
    Sent = $true
    Type = $Type
    EventId = "task:${Source}:${Id}"
}
