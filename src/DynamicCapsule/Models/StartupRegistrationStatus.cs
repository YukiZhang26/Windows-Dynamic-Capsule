namespace DynamicCapsule.Models;

internal sealed record StartupRegistrationStatus(
    bool IsEnabled,
    bool IsValid,
    string Message);
