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

    [Fact]
    public void RejectedCallsIgnoreUnrelatedChangingPayloadValues()
    {
        var signatures = new Dictionary<string, int>(StringComparer.Ordinal);
        const string rejection = "TOOL_ARGUMENT_ERROR {\"code\":\"unknown_property\",\"path\":\"$.includeResolved\",\"message\":\"not valid for this op\"}";

        Assert.Equal(1, ProjectToolCallConvergence.RegisterRejectedCall(signatures,
            "project_directive", "{\"op\":\"acknowledge\",\"directiveID\":\"d1\",\"note\":\"first wording\",\"includeResolved\":false}", rejection));
        Assert.Equal(2, ProjectToolCallConvergence.RegisterRejectedCall(signatures,
            "project_directive", "{\"op\":\"acknowledge\",\"directiveID\":\"d1\",\"note\":\"regenerated wording\",\"includeResolved\":false}", rejection));
        Assert.Equal(3, ProjectToolCallConvergence.RegisterRejectedCall(signatures,
            "project_directive", "{\"op\":\"acknowledge\",\"directiveID\":\"d2\",\"note\":\"another attempt\",\"includeResolved\":false}", rejection));
    }

    [Fact]
    public void RejectedCallsKeepDifferentOperationsSeparate()
    {
        var signatures = new Dictionary<string, int>(StringComparer.Ordinal);
        const string rejection = "same structural failure";

        Assert.Equal(1, ProjectToolCallConvergence.RegisterRejectedCall(signatures,
            "checkpoint", "{\"op\":\"get\",\"result\":\"done\"}", rejection));
        Assert.Equal(1, ProjectToolCallConvergence.RegisterRejectedCall(signatures,
            "checkpoint", "{\"op\":\"complete_step\",\"result\":\"done\"}", rejection));
    }

    [Fact]
    public void ContractErrorIdentityUsesCodeAndPathInsteadOfChangingProse()
    {
        var signatures = new Dictionary<string, int>(StringComparer.Ordinal);
        const string first = "TOOL_ARGUMENT_ERROR {\"code\":\"unknown_property\",\"path\":\"$.includeResolved\",\"message\":\"first wording\"}";
        const string second = "TOOL_ARGUMENT_ERROR {\"code\":\"unknown_property\",\"path\":\"$.includeResolved\",\"message\":\"different wording\",\"suggestion\":\"try list\"}";
        const string otherPath = "TOOL_ARGUMENT_ERROR {\"code\":\"unknown_property\",\"path\":\"$.result\",\"message\":\"different field\"}";

        Assert.Equal(1, ProjectToolCallConvergence.RegisterRejectedCall(signatures,
            "project_directive", "{\"op\":\"acknowledge\"}", first));
        Assert.Equal(2, ProjectToolCallConvergence.RegisterRejectedCall(signatures,
            "project_directive", "{\"op\":\"acknowledge\"}", second));
        Assert.Equal(1, ProjectToolCallConvergence.RegisterRejectedCall(signatures,
            "project_directive", "{\"op\":\"acknowledge\"}", otherPath));
    }
}
