namespace DynamicCapsule.Models;

internal sealed record DiagnosticsSnapshot(
    string Process,
    string Package,
    string Media,
    string Notifications,
    string TaskPipe,
    string Timer,
    string ActiveEvent,
    string Configuration,
    string Startup,
    string Accessibility);
