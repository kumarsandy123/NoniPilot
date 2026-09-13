namespace NoniPilot.Domain.Models;

public sealed class Plugin
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public List<string> Capabilities { get; init; } = new();
    public List<string> Permissions { get; init; } = new();
    public bool Enabled { get; set; }
}

public sealed class DeviceProfile
{
    public required string Id { get; init; }
    public required string MachineName { get; init; }
    public required string OperatingSystem { get; init; }
    public required string AgentVersion { get; init; }
    public List<string> Capabilities { get; init; } = new();
    public string Health { get; set; } = "unknown";
}

/// <summary>
/// A pointer to a secret held in Windows Credential Manager/DPAPI - never the plaintext secret itself.
/// See section 9 / 18 of the roadmap: no plaintext credentials are ever stored by NoniPilot.
/// </summary>
public sealed class CredentialReference
{
    public required string Id { get; init; }
    public required string Provider { get; init; }
    public required string Target { get; init; }
    public required string SecureStoreKey { get; init; }
}
