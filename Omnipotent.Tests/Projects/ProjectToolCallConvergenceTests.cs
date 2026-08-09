using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects;

public class ProjectToolCallConvergenceTests
{
    [Fact]
    public void RejectedCallsUseSemanticJsonSignatures()
    {
        var signatures = new Dictionary<string, int>(StringComparer.Ordinal);
        const string rejection = "TOOL_ARGUMENT_ERROR unknown property";

        Assert.Equal(1, ProjectToolCallConvergence.RegisterRejectedCall(signatures,
            "retire_sub_agent", "{\"role\":\"\",\"agentID\":\"worker\"}", rejection));
        Assert.Equal(2, ProjectToolCallConvergence.RegisterRejectedCall(signatures,
            "retire_sub_agent", "{ \"agentID\": \"worker\", \"role\": \"\" }", rejection));
        Assert.Equal(3, ProjectToolCallConvergence.RegisterRejectedCall(signatures,
            "retire_sub_agent", "{\"agentID\":\"worker\",\"role\":\"\"}", rejection));
    }

    [Fact]
    public void CorrectedCallDoesNotInheritRejectedCallCount()
    {
        var signatures = new Dictionary<string, int>(StringComparer.Ordinal);

        Assert.Equal(1, ProjectToolCallConvergence.RegisterRejectedCall(signatures,
            "retire_sub_agent", "{\"agentID\":\"worker\"}", "validation failed"));
        Assert.Equal(1, ProjectToolCallConvergence.RegisterCall(signatures,
            "retire_sub_agent", "{\"agentID\":\"worker\"}"));
    }

    [Fact]
    public void DifferentRejectionReasonsDoNotCreateFalseLoopTrips()
    {
        var signatures = new Dictionary<string, int>(StringComparer.Ordinal);

        Assert.Equal(1, ProjectToolCallConvergence.RegisterRejectedCall(signatures,
            "some_tool", "{\"value\":1}", "first validation failure"));
        Assert.Equal(1, ProjectToolCallConvergence.RegisterRejectedCall(signatures,
            "some_tool", "{\"value\":1}", "different policy failure"));
    }
}
