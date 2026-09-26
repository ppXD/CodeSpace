using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the executor's write-scope wiring: only a run allowed to write its workspace gets it bound writable, and a
/// scope this code does not know fails closed to read-only. The executor applying it on every launch and revise
/// round is pinned one tier up, in <c>AgentRunExecutorTests</c>, against the spec the runner is actually handed.
/// </summary>
[Trait("Category", "Unit")]
public class AgentRunExecutorWriteScopeTests
{
    [Theory]
    [InlineData(AgentWriteScope.Workspace, false)]
    [InlineData(AgentWriteScope.ReadOnly, true)]
    [InlineData((AgentWriteScope)99, true)]
    public void Only_a_workspace_write_scope_leaves_the_workspace_writable(AgentWriteScope scope, bool expectedReadOnly)
    {
        var spec = AgentRunExecutor.ApplyWriteScope(new SandboxSpec { Command = "agent", WorkingDirectory = "/work/ws" }, new AgentPermissions { WriteScope = scope });

        spec.ReadOnlyWorkingDirectory.ShouldBe(expectedReadOnly, $"write scope {scope} must {(expectedReadOnly ? "not " : "")}leave the workspace writable");
    }

    [Theory]
    [InlineData(AgentAutonomyLevel.Confined, true)]
    [InlineData(AgentAutonomyLevel.Standard, false)]
    [InlineData(AgentAutonomyLevel.Trusted, false)]
    [InlineData(AgentAutonomyLevel.Unleashed, false)]
    public void Each_tier_s_derived_write_scope_reaches_the_spec(AgentAutonomyLevel level, bool expectedReadOnly) =>
        AgentRunExecutor.ApplyWriteScope(new SandboxSpec { Command = "agent" }, AgentAutonomyPolicy.Derive(level)).ReadOnlyWorkingDirectory.ShouldBe(expectedReadOnly);
}
