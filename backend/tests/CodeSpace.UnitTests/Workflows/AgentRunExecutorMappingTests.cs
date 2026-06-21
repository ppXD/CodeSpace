using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class AgentRunExecutorMappingTests
{
    private static SandboxResult Sandbox(SandboxStatus status) => new() { Status = status, ExitCode = -1, Stdout = "", Stderr = "" };

    [Fact]
    public void A_stalled_sandbox_maps_to_needs_review_blocked()
    {
        // C3: a stalled run (no output for the idle window — likely a nested prompt) is surfaced for a human as
        // NeedsReview(Blocked), not buried as a bare timeout. The harness is not consulted for a non-exit terminal.
        var result = AgentRunExecutor.MapSandboxResult(Sandbox(SandboxStatus.Stalled), harness: null!, System.Array.Empty<AgentEvent>());

        result.Status.ShouldBe(AgentRunStatus.NeedsReview);
        result.CompletionDisposition.ShouldBe(CompletionDisposition.Blocked);
        result.ExitReason.ShouldBe("stalled");
        result.Error.ShouldContain("stalled");
    }

    [Fact]
    public void A_timed_out_sandbox_still_maps_to_timed_out()
    {
        var result = AgentRunExecutor.MapSandboxResult(Sandbox(SandboxStatus.TimedOut), harness: null!, System.Array.Empty<AgentEvent>());

        result.Status.ShouldBe(AgentRunStatus.TimedOut);
        result.ExitReason.ShouldBe("timed-out");
    }
}
