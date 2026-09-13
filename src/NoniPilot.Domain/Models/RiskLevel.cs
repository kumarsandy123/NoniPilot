namespace NoniPilot.Domain.Models;

/// <summary>
/// The four permission tiers from the NoniPilot product spec (section 9).
/// Every executable action must be classified into one of these before it runs.
/// </summary>
public enum RiskLevel
{
    /// <summary>Open apps, read directories, search, scroll, inspect. Executes automatically.</summary>
    L0Safe = 0,

    /// <summary>Create folders, rename, move non-sensitive files, download. Policy-checked, configurable confirmation.</summary>
    L1Normal = 1,

    /// <summary>Delete files, install software, run elevated tools, change settings. Requires explicit confirmation.</summary>
    L2Sensitive = 2,

    /// <summary>Format disk, destructive bulk deletion, disable security controls. Always requires confirmation + policy authorization.</summary>
    L3Critical = 3,
}
