using System.Reflection;
using Omnipotent.Services.KliveAgent;
using Omnipotent.Services.Projects;
using Omnipotent.Services.Projects.Containers;

namespace Omnipotent.Tests.Projects;

/// <summary>
/// Guards the execution-unblock changes: the script-API reference can't silently drift from the
/// real script surface, the verification-code extractor behaves, and the desktop-container
/// staleness comparison recreates exactly the containers it should.
/// </summary>
public class ExecutionUnblockTests
{
    // ── WS4: the script-API cheat-sheet must cover every method the project script host exposes ──

    [Fact]
    public void ScriptApiReference_CoversEveryPublicMethodOfTheProjectScriptHost()
    {
        string reference = ScriptGlobals.BuildApiReference(typeof(ProjectCommanderTools.WorkScriptGlobals));

        var methodNames = typeof(ProjectCommanderTools.WorkScriptGlobals)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.DeclaringType != typeof(object) && !m.IsSpecialName && !m.Name.StartsWith('<'))
            .Select(m => m.Name)
            .Distinct();

        foreach (var name in methodNames)
            Assert.True(reference.Contains(name + "(", StringComparison.Ordinal),
                $"Script API reference is missing method '{name}' — it would drift from the real surface.");
    }

    [Fact]
    public void ScriptApiReference_LeadsWithTheGotchasThatBurnedWakes()
    {
        string reference = ScriptGlobals.BuildApiReference(typeof(ProjectCommanderTools.WorkScriptGlobals));

        // The exact traps from the transcript: GetServiceMember arg type, awaiting RunBash, and
        // that locals don't cross script blocks.
        Assert.Contains("GetServiceMember", reference, StringComparison.Ordinal);
        Assert.Contains("RunBash", reference, StringComparison.Ordinal);
        Assert.Contains("ONE block", reference, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptApiReference_RendersReadableSignatures_NotRawReflectionNames()
    {
        string reference = ScriptGlobals.BuildApiReference(typeof(ProjectCommanderTools.WorkScriptGlobals));

        // GetService(string serviceName) -> object — the readable form, not "System.String".
        Assert.Contains("GetService(string serviceName) -> object", reference, StringComparison.Ordinal);
        Assert.DoesNotContain("System.String", reference, StringComparison.Ordinal);
        Assert.DoesNotContain("`1", reference, StringComparison.Ordinal); // no raw generic arity
    }

    [Fact]
    public void ReflectionMembers_ExposeConventionalCompatibilityAliases()
    {
        var member = new Omnipotent.Services.KliveAgent.Models.AgentObjectMember
        {
            Kind = "method", Type = "Task<string>", Name = "Fetch",
        };

        Assert.Equal("method", member.MemberType);
        Assert.Equal("Task<string>", member.TypeName);
        Assert.NotNull(typeof(ProjectCommanderTools.WorkScriptGlobals).GetProperty("Globals"));
        Assert.NotNull(typeof(ProjectCommanderTools.WorkScriptGlobals).GetMethod("RunBashAsync"));
        Assert.NotNull(typeof(ProjectCommanderTools.WorkScriptGlobals).GetMethod("ToSafeJson"));
    }

    // ── WS3: verification-code extraction ──

    [Theory]
    [InlineData("Your TikTok code is 483920. Do not share it.", "483920")]
    [InlineData("Verification code: 12345", "12345")]
    [InlineData("Enter 8391 to confirm your email", "8391")]
    [InlineData("<p>Your one-time code is <b>771204</b></p>", "771204")]
    public void ExtractVerificationCode_FindsTheCode(string body, string expected)
    {
        Assert.Equal(expected, ProjectCommanderTools.ExtractVerificationCode(body));
    }

    [Fact]
    public void ExtractVerificationCode_PrefersACuedCodeOverAnIncidentalNumber()
    {
        // A long order-ish number appears first, but the real code sits next to the "code" cue.
        string body = "Order 100 confirmed. Your verification code is 246810.";
        Assert.Equal("246810", ProjectCommanderTools.ExtractVerificationCode(body));
    }

    [Theory]
    [InlineData("No numbers here at all")]
    [InlineData("")]
    [InlineData("Only a 3-digit 123 run that's too short")]
    public void ExtractVerificationCode_ReturnsNullWhenThereIsNoCode(string body)
    {
        Assert.Null(ProjectCommanderTools.ExtractVerificationCode(body));
    }

    // ── WS2: stale-container comparison ──

    [Fact]
    public void IsStaleAgainst_UnknownCurrentImageHash_IsNeverStale()
    {
        // A host that can't rebuild (missing/unlabelled image) must not churn its containers.
        Assert.False(ContainerOrchestrator.IsStaleAgainst("abc123", null));
        Assert.False(ContainerOrchestrator.IsStaleAgainst("abc123", ""));
    }

    [Fact]
    public void IsStaleAgainst_MatchingHash_IsCurrent()
    {
        Assert.False(ContainerOrchestrator.IsStaleAgainst("abc123", "abc123"));
    }

    [Fact]
    public void IsStaleAgainst_DifferentHash_IsStale()
    {
        Assert.True(ContainerOrchestrator.IsStaleAgainst("abc123", "def456"));
    }

    [Fact]
    public void IsStaleAgainst_LegacyContainerWithNoStamp_IsStaleSoItGetsRecreatedOnce()
    {
        // The exact case behind the transcript: a container created before chromium/browser-inspect
        // were baked in has no stamped hash and must be recreated to pick them up.
        Assert.True(ContainerOrchestrator.IsStaleAgainst("", "def456"));
        Assert.True(ContainerOrchestrator.IsStaleAgainst(null, "def456"));
    }
}
