using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public class ToolDisplayHelperTests
{
    [Fact]
    public void FormatToolStatusName_Task_UsesFriendlyAgentLabel()
    {
        var status = ToolDisplayHelper.FormatToolStatusName("task", "{\"agent_type\":\"explore\"}");

        Assert.Equal("Running explore", status);
    }

    [Fact]
    public void FormatToolStatusName_AgentTool_UsesFriendlyAgentLabel()
    {
        var status = ToolDisplayHelper.FormatToolStatusName("agent:Coding Lumi");

        Assert.Equal("Running Coding Lumi", status);
    }

    [Fact]
    public void FormatProgressLabel_AppendsEllipsisWithoutDuplicatingRunningPrefix()
    {
        var status = ToolDisplayHelper.FormatProgressLabel("Running command");

        Assert.Equal("Running command…", status);
        Assert.DoesNotContain("Running Running", status);
    }

    [Fact]
    public void FormatProgressLabel_PreservesExistingEllipsis()
    {
        var status = ToolDisplayHelper.FormatProgressLabel("Thinking…");

        Assert.Equal("Thinking…", status);
    }

    [Fact]
    public void FormatProgressLabel_LeavesStandaloneActionPhraseIntact()
    {
        var baseLabel = ToolDisplayHelper.FormatToolStatusName("view", "{\"path\":\"E:\\\\repo\\\\sample.txt\"}");
        var status = ToolDisplayHelper.FormatProgressLabel(baseLabel);

        Assert.Equal("Reading sample.txt…", status);
    }
}
