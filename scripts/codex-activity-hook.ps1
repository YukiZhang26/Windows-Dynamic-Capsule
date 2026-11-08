# Local status bridge. Only the latest timestamp and delivery result per phase are retained.
$ErrorActionPreference = 'Stop'
try {
    $settings = Join-Path $env:LOCALAPPDATA 'WindowsDynamicCapsule\settings.json'
    if (([IO.File]::ReadAllText($settings) | ConvertFrom-Json).monitorAiActivity -ne $true) { exit 0 }
    [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
    $event = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $id = [string]$event.session_id
    if ($id -notmatch '^[a-zA-Z0-9._:-]{1,120}$') { exit 0 }
    $type, $label = switch ($event.hook_event_name) {
        'UserPromptSubmit' { 'task.started'; 'Running' }
        'Stop' { 'task.completed'; 'Completed' }
        'Interrupt' { 'task.cancelled'; 'Cancelled' }
        default { exit 0 }
    }
    $payload = @{version=1; type=$type; id=$id; source='Codex'; title="Codex - $label"; message=$label} | ConvertTo-Json -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($payload + [Environment]::NewLine)
    $result = 'not-delivered'
    for ($attempt=0; $attempt -lt 3; $attempt++) {
        $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'DynamicCapsule.v1', [IO.Pipes.PipeDirection]::Out)
        try {
            $pipe.Connect(320)
            $pipe.Write($bytes,0,$bytes.Length)
            $pipe.Flush()
            $result = 'sent'
            break
        } catch [TimeoutException] { } catch [IO.IOException] { }
        finally { $pipe.Dispose() }
    }
    [IO.File]::WriteAllText((Join-Path (Split-Path $settings) "hook-$label.status"), ([DateTimeOffset]::UtcNow.ToString('o') + ' ' + $result))
} catch { }
exit 0
