using NoniPilot.Agent.Providers;
using NoniPilot.Agent.Tools;

namespace NoniPilot.Tests.Unit;

public class ToolRelevanceFilterTests
{
    private static ToolSpec MakeTool(string name, string policyTool) => new()
    {
        ClaudeName = name,
        PolicyTool = policyTool,
        PolicyAction = "Test",
        Definition = new ChatToolDefinition(name, "test tool", ToolSpec.Schema(new { type = "object", properties = new { } })),
        ExecuteAsync = (_, _) => Task.FromResult<object?>(null),
    };

    private static readonly IReadOnlyList<ToolSpec> SampleTools =
    [
        MakeTool("filesystem_list_directory", "FileSystem"),
        MakeTool("application_launch", "Application"),
        MakeTool("computercontrol_click", "ComputerControl"),
    ];

    [Fact]
    public void SelectRelevantTools_ComputerControlOnlyCommand_DropsUnrelatedCategories()
    {
        var selected = ToolRelevanceFilter.SelectRelevantTools("click at 500,300", SampleTools);

        Assert.Contains(selected, t => t.ClaudeName == "computercontrol_click");
        Assert.DoesNotContain(selected, t => t.ClaudeName == "filesystem_list_directory");
        Assert.DoesNotContain(selected, t => t.ClaudeName == "application_launch");
    }

    [Fact]
    public void SelectRelevantTools_FileSystemCommand_KeepsFileSystemDropsComputerControl()
    {
        var selected = ToolRelevanceFilter.SelectRelevantTools("create a new folder called Backup", SampleTools);

        Assert.Contains(selected, t => t.ClaudeName == "filesystem_list_directory");
        Assert.DoesNotContain(selected, t => t.ClaudeName == "computercontrol_click");
    }

    [Fact]
    public void SelectRelevantTools_ApplicationLaunchCommand_KeepsApplicationDropsComputerControl()
    {
        var selected = ToolRelevanceFilter.SelectRelevantTools("open chrome", SampleTools);

        Assert.Contains(selected, t => t.ClaudeName == "application_launch");
        // "open" also matches the FileSystem keyword set, which is fine (open-ended commands
        // legitimately might need both) - the guarantee under test is only that a clearly
        // irrelevant category (ComputerControl) is excluded.
        Assert.DoesNotContain(selected, t => t.ClaudeName == "computercontrol_click");
    }

    [Fact]
    public void SelectRelevantTools_NoCategoryKeywordsMatched_ReturnsEverything()
    {
        var selected = ToolRelevanceFilter.SelectRelevantTools("what time is it right now", SampleTools);

        Assert.Equal(SampleTools.Count, selected.Count);
    }
}
