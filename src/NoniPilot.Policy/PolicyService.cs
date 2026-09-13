using NoniPilot.Domain.Interfaces;
using NoniPilot.Domain.Models;
using NoniPilot.Domain.Security;

namespace NoniPilot.Policy;

/// <summary>
/// The default IPolicyService: classifies every tool/action against the built-in risk table
/// (section 9), lets an administrator PermissionPolicy override that classification for a
/// given scope, and fails closed - an action this service has never heard of is treated as
/// L2 Sensitive (always-confirm), never auto-allowed.
/// </summary>
public sealed class PolicyService : IPolicyService
{
    /// <summary>Read-only view of the built-in risk table, for a Settings page to display - not used for evaluation itself.</summary>
    public static IReadOnlyDictionary<string, RiskLevel> DefaultClassificationTable => DefaultClassification;

    // Roadmap section 9's worked examples, expressed as "Tool.Action" -> RiskLevel.
    private static readonly Dictionary<string, RiskLevel> DefaultClassification = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FileSystem.ListDirectory"] = RiskLevel.L0Safe,
        ["FileSystem.Search"] = RiskLevel.L0Safe,
        ["FileSystem.CreateDirectory"] = RiskLevel.L1Normal,
        ["FileSystem.Copy"] = RiskLevel.L1Normal,
        ["FileSystem.Move"] = RiskLevel.L1Normal,
        ["FileSystem.Rename"] = RiskLevel.L1Normal,
        ["FileSystem.Delete"] = RiskLevel.L2Sensitive,
        ["FileSystem.OpenBestMatch"] = RiskLevel.L1Normal, // same risk tier as Application.Launch - it only ever opens something already inside a given parent folder
        ["FileSystem.CreateFile"] = RiskLevel.L1Normal, // same tier as CreateDirectory

        ["Application.ListRunning"] = RiskLevel.L0Safe,
        ["Application.Focus"] = RiskLevel.L0Safe,
        ["Application.Launch"] = RiskLevel.L1Normal,
        ["Application.Close"] = RiskLevel.L1Normal,

        ["ComputerControl.MoveMouse"] = RiskLevel.L0Safe,
        ["ComputerControl.Scroll"] = RiskLevel.L0Safe,
        ["ComputerControl.FocusWindow"] = RiskLevel.L0Safe,
        ["ComputerControl.Click"] = RiskLevel.L1Normal,
        ["ComputerControl.Drag"] = RiskLevel.L1Normal,
        ["ComputerControl.TypeText"] = RiskLevel.L1Normal,
        ["ComputerControl.SendKeys"] = RiskLevel.L1Normal,

        ["Gesture.CheckVisibility"] = RiskLevel.L0Safe, // read-only status report, no side effects
        ["Face.Recognize"] = RiskLevel.L0Safe, // read-only status report, no side effects
        ["Face.Enroll"] = RiskLevel.L0Safe, // writes only to NoniPilot's own local model file, nothing system-wide

        ["Browser.OpenUrl"] = RiskLevel.L1Normal, // same tier as Application.Launch - it only opens a URL in the default browser

        // Dashboard/System Tools additions - explicit, not just fail-closed-by-accident, since
        // these are genuinely sensitive (they affect the whole machine, not just this app).
        ["System.Screenshot"] = RiskLevel.L1Normal,
        ["System.Shutdown"] = RiskLevel.L2Sensitive,
        ["System.Restart"] = RiskLevel.L2Sensitive,
        ["System.Lock"] = RiskLevel.L2Sensitive,
    };

    private readonly IReadOnlyList<PermissionPolicy> _adminPolicies;

    public PolicyService(IReadOnlyList<PermissionPolicy>? adminPolicies = null)
    {
        _adminPolicies = adminPolicies ?? Array.Empty<PermissionPolicy>();
    }

    public PolicyDecision Evaluate(string tool, string action, IReadOnlyDictionary<string, object?> parameters)
    {
        var scopeKey = $"{tool}.{action}";

        // Fail closed: an action this table has never seen is Sensitive, not Safe.
        var risk = DefaultClassification.GetValueOrDefault(scopeKey, RiskLevel.L2Sensitive);

        if (TargetsProtectedPath(parameters))
        {
            risk = RiskLevel.L3Critical;
        }

        var overridePolicy = _adminPolicies.FirstOrDefault(p =>
            string.Equals(p.Scope, scopeKey, StringComparison.OrdinalIgnoreCase) || p.Scope == "*");

        if (overridePolicy is not null)
        {
            risk = overridePolicy.RiskLevel;

            if (overridePolicy.PathOrAppRestrictions.Count > 0 && !MatchesAnyRestriction(parameters, overridePolicy.PathOrAppRestrictions))
            {
                // Restrictions defined but this target isn't one of them - policy doesn't apply, fall back to default risk-based mode.
                overridePolicy = null;
            }
        }

        var approvalMode = overridePolicy?.ApprovalMode ?? DefaultApprovalModeFor(risk);

        if (approvalMode == ApprovalMode.Deny)
        {
            return new PolicyDecision(false, risk, approvalMode, $"'{scopeKey}' is denied by administrator policy.");
        }

        var reason = approvalMode == ApprovalMode.AlwaysConfirm
            ? $"'{scopeKey}' is risk level {risk} and requires explicit user confirmation."
            : $"'{scopeKey}' is risk level {risk} and is auto-allowed.";

        return new PolicyDecision(true, risk, approvalMode, reason);
    }

    private static ApprovalMode DefaultApprovalModeFor(RiskLevel risk) => risk switch
    {
        RiskLevel.L0Safe => ApprovalMode.AutoAllow,
        RiskLevel.L1Normal => ApprovalMode.AutoAllow,
        RiskLevel.L2Sensitive => ApprovalMode.AlwaysConfirm,
        RiskLevel.L3Critical => ApprovalMode.AlwaysConfirm,
        _ => ApprovalMode.AlwaysConfirm,
    };

    private static bool TargetsProtectedPath(IReadOnlyDictionary<string, object?> parameters)
    {
        foreach (var key in new[] { "path", "sourcePath", "destinationPath", "appPathOrName" })
        {
            if (parameters.TryGetValue(key, out var value) && value is string s && ProtectedPaths.IsProtected(s))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesAnyRestriction(IReadOnlyDictionary<string, object?> parameters, IReadOnlyList<string> restrictions)
    {
        foreach (var key in new[] { "path", "sourcePath", "destinationPath", "appPathOrName" })
        {
            if (parameters.TryGetValue(key, out var value) && value is string s)
            {
                foreach (var pattern in restrictions)
                {
                    // Simple prefix/glob-lite match: "C:\Windows\**" -> prefix "C:\Windows\"
                    var prefix = pattern.Replace("**", string.Empty).Replace("*", string.Empty);
                    if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
