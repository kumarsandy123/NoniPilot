using NoniPilot.Domain.Models;
using NoniPilot.Policy;

namespace NoniPilot.Tests.Unit;

public class PolicyServiceTests
{
    [Theory]
    [InlineData("FileSystem", "ListDirectory", RiskLevel.L0Safe)]
    [InlineData("FileSystem", "CreateDirectory", RiskLevel.L1Normal)]
    [InlineData("FileSystem", "Delete", RiskLevel.L2Sensitive)]
    [InlineData("ComputerControl", "MoveMouse", RiskLevel.L0Safe)]
    [InlineData("ComputerControl", "Click", RiskLevel.L1Normal)]
    public void Evaluate_ClassifiesKnownActions_AtTheirDocumentedRiskLevel(string tool, string action, RiskLevel expected)
    {
        var policy = new PolicyService();

        var decision = policy.Evaluate(tool, action, new Dictionary<string, object?>());

        Assert.Equal(expected, decision.RiskLevel);
    }

    [Fact]
    public void Evaluate_UnknownAction_FailsClosedToSensitive()
    {
        var policy = new PolicyService();

        var decision = policy.Evaluate("SomeNewTool", "SomeNewAction", new Dictionary<string, object?>());

        Assert.Equal(RiskLevel.L2Sensitive, decision.RiskLevel);
        Assert.Equal(ApprovalMode.AlwaysConfirm, decision.ApprovalMode);
    }

    [Fact]
    public void Evaluate_L0AndL1_AreAutoAllowedByDefault()
    {
        var policy = new PolicyService();

        var safe = policy.Evaluate("FileSystem", "ListDirectory", new Dictionary<string, object?>());
        var normal = policy.Evaluate("FileSystem", "CreateDirectory", new Dictionary<string, object?>());

        Assert.Equal(ApprovalMode.AutoAllow, safe.ApprovalMode);
        Assert.Equal(ApprovalMode.AutoAllow, normal.ApprovalMode);
    }

    [Fact]
    public void Evaluate_SensitiveAndCriticalActions_RequireConfirmation()
    {
        var policy = new PolicyService();

        var decision = policy.Evaluate("FileSystem", "Delete", new Dictionary<string, object?>());

        Assert.Equal(ApprovalMode.AlwaysConfirm, decision.ApprovalMode);
        Assert.True(decision.Allowed); // confirmable, not outright denied
    }

    [Fact]
    public void Evaluate_TargetingWindowsFolder_EscalatesToCritical()
    {
        var policy = new PolicyService();
        var windowsPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var decision = policy.Evaluate("FileSystem", "CreateDirectory", new Dictionary<string, object?>
        {
            ["path"] = System.IO.Path.Combine(windowsPath, "System32", "evil"),
        });

        Assert.Equal(RiskLevel.L3Critical, decision.RiskLevel);
    }

    [Fact]
    public void Evaluate_AdministratorDenyPolicy_BlocksTheAction()
    {
        var policy = new PolicyService(new[]
        {
            new PermissionPolicy
            {
                Id = "block-delete",
                Scope = "FileSystem.Delete",
                RiskLevel = RiskLevel.L2Sensitive,
                ApprovalMode = ApprovalMode.Deny,
            },
        });

        var decision = policy.Evaluate("FileSystem", "Delete", new Dictionary<string, object?>());

        Assert.False(decision.Allowed);
    }
}
