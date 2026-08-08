namespace DynamicCapsule.Models;

internal sealed record SingleInstanceCommand(
    int Version,
    string[] Arguments,
    string WorkingDirectory,
    DateTimeOffset Timestamp);
