using System.Text.RegularExpressions;
using CodeSpace.Messages.Dtos.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The shape and the literal of the one statement a non-capturing process may make about a run's logs. Both are
/// durable: the code is written into <c>agent_run_log_stream.error_code</c> and read back by the Room and by
/// operators, so a rename here silently strands every stream already carrying the old value.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentRunLogOwnerLossRequestTests
{
    /// <summary>Rule 8 pin: renaming this constant is a decision, not an invisible refactor.</summary>
    [Fact]
    public void Owner_lost_error_code_is_pinned_to_its_literal()
    {
        AgentRunLogOwnerLossRequest.OwnerLostErrorCode.ShouldBe("capture.owner-lost");
    }

    /// <summary>
    /// The code must survive the seam's own validation and the column's check constraint, or the flip would be
    /// refused at the database with nothing in the request to explain why.
    /// </summary>
    [Fact]
    public void Owner_lost_error_code_satisfies_the_streams_error_code_contract()
    {
        var code = AgentRunLogOwnerLossRequest.OwnerLostErrorCode;

        code.Length.ShouldBeLessThanOrEqualTo(128, "agent_run_log_stream.error_code is VARCHAR(128)");
        Regex.IsMatch(code, "^[a-z0-9][a-z0-9.-]{0,127}$").ShouldBeTrue("AgentRunLogService.ErrorCodePattern rejects anything else before the write is attempted");
    }

    [Fact]
    public void The_request_carries_the_callers_own_generation_and_nothing_about_the_streams()
    {
        var teamId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        var request = new AgentRunLogOwnerLossRequest(teamId, runId, 2, AgentRunLogOwnerLossRequest.OwnerLostErrorCode);

        request.TeamId.ShouldBe(teamId);
        request.AgentRunId.ShouldBe(runId);
        request.WorkerFenceEpoch.ShouldBe(2, "the abandon's OWN fence — the streams to orphan are the ones strictly behind it, which the caller never has to enumerate");
        request.ErrorCode.ShouldBe("capture.owner-lost");
        request.ShouldBe(new AgentRunLogOwnerLossRequest(teamId, runId, 2, "capture.owner-lost"), "value equality keeps a retried abandon's statement identical");
    }
}
