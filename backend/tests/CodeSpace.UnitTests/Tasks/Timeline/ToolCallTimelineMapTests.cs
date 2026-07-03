using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Tasks.Timeline;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks.Timeline;

/// <summary>
/// The pure tool-call → timeline mapping: severity + level ride the CLOSED <see cref="ToolCallLedgerStatus"/> axis
/// (a failed/denied side effect is an Error milestone; a landed one folds to a success Detail); the title is
/// tool-neutral ("Called {kind}"); the error rides the summary; the agent + node are stamped on. No DB.
/// </summary>
[Trait("Category", "Unit")]
public class ToolCallTimelineMapTests
{
    private static readonly Guid AgentId = Guid.NewGuid();
    private static readonly IReadOnlyDictionary<Guid, string?> NodeByAgent = new Dictionary<Guid, string?> { [AgentId] = "code" };

    private static ToolCallLedger Call(ToolCallLedgerStatus status = ToolCallLedgerStatus.Succeeded, string toolKind = "git.open_pr", string? error = null, Guid? agentId = null) => new()
    {
        Id = Guid.NewGuid(),
        AgentRunId = agentId ?? AgentId,
        ToolKind = toolKind,
        Status = status,
        Error = error,
        CreatedDate = DateTimeOffset.UtcNow,
    };

    [Theory]
    [InlineData(ToolCallLedgerStatus.Succeeded, TimelineSeverity.Success)]
    [InlineData(ToolCallLedgerStatus.Failed, TimelineSeverity.Error)]
    [InlineData(ToolCallLedgerStatus.Denied, TimelineSeverity.Error)]
    [InlineData(ToolCallLedgerStatus.Expired, TimelineSeverity.Warning)]
    [InlineData(ToolCallLedgerStatus.Pending, TimelineSeverity.Info)]
    [InlineData(ToolCallLedgerStatus.Running, TimelineSeverity.Info)]
    [InlineData(ToolCallLedgerStatus.AwaitingApproval, TimelineSeverity.Info)]
    public void Severity_rides_the_status_axis(ToolCallLedgerStatus status, TimelineSeverity expected)
    {
        var ev = ToolCallTimelineMap.ToEvent(Call(status), NodeByAgent);

        ev.Severity.ShouldBe(expected);
        ev.SourceKey.ShouldBe(ToolCallTimelineMap.Key);
    }

    [Theory]
    // A side effect that DIDN'T LAND — failed, denied, or an approval that expired unrun — is a story milestone; a
    // landed / in-flight call folds to Detail under its wave.
    [InlineData(ToolCallLedgerStatus.Failed, TimelineLevel.Milestone)]
    [InlineData(ToolCallLedgerStatus.Denied, TimelineLevel.Milestone)]
    [InlineData(ToolCallLedgerStatus.Expired, TimelineLevel.Milestone)]
    [InlineData(ToolCallLedgerStatus.Succeeded, TimelineLevel.Detail)]
    [InlineData(ToolCallLedgerStatus.Pending, TimelineLevel.Detail)]
    [InlineData(ToolCallLedgerStatus.Running, TimelineLevel.Detail)]
    [InlineData(ToolCallLedgerStatus.AwaitingApproval, TimelineLevel.Detail)]
    public void A_side_effect_that_did_not_land_is_a_milestone_the_rest_fold(ToolCallLedgerStatus status, TimelineLevel expected)
    {
        ToolCallTimelineMap.ToEvent(Call(status), NodeByAgent).Level.ShouldBe(expected);
    }

    [Fact]
    public void Every_status_maps_to_a_defined_severity_and_level()
    {
        // A new ToolCallLedgerStatus member must get a considered mapping — iterate the whole enum so an unmapped
        // addition is caught here (it would fall to the Info/Detail default, which this test makes a visible decision).
        foreach (var status in Enum.GetValues<ToolCallLedgerStatus>())
        {
            var ev = ToolCallTimelineMap.ToEvent(Call(status), NodeByAgent);
            Enum.IsDefined(ev.Severity).ShouldBeTrue($"{status} maps to a defined severity");
            Enum.IsDefined(ev.Level).ShouldBeTrue($"{status} maps to a defined level");
        }
    }

    [Fact]
    public void An_unknown_future_status_falls_to_the_safe_info_detail_default()
    {
        // A status value outside today's vocabulary (a forward-compat guard) degrades to Info/Detail rather than crashing.
        var ev = ToolCallTimelineMap.ToEvent(Call((ToolCallLedgerStatus)999), NodeByAgent);

        ev.Severity.ShouldBe(TimelineSeverity.Info);
        ev.Level.ShouldBe(TimelineLevel.Detail);
    }

    [Fact]
    public void Stamps_the_id_kind_title_agent_node_and_a_tool_neutral_title()
    {
        var call = Call(toolKind: "git.commit");

        var ev = ToolCallTimelineMap.ToEvent(call, NodeByAgent);

        ev.Id.ShouldBe($"tool-{call.Id:N}");
        ev.Kind.ShouldBe("tool.git.commit", "the provenance kind embeds the tool kind (never switched on)");
        ev.Title.ShouldBe("Called git.commit", "the title is tool-neutral — no per-tool copy coupling");
        ev.AgentRunId.ShouldBe(AgentId.ToString());
        ev.NodeId.ShouldBe("code");
        ev.Order.ShouldBe(0, "the ledger has no Sequence — the same-tick tie-break falls to Id");
    }

    [Fact]
    public void Occurred_at_is_the_ledger_created_date()
    {
        var call = Call();

        ToolCallTimelineMap.ToEvent(call, NodeByAgent).OccurredAt.ShouldBe(call.CreatedDate);
    }

    [Fact]
    public void The_error_rides_the_summary_on_a_failure_and_is_null_on_success()
    {
        ToolCallTimelineMap.ToEvent(Call(ToolCallLedgerStatus.Failed, error: "remote rejected: protected branch"), NodeByAgent)
            .Summary.ShouldBe("remote rejected: protected branch");

        ToolCallTimelineMap.ToEvent(Call(ToolCallLedgerStatus.Succeeded), NodeByAgent)
            .Summary.ShouldBeNull("a landed call carries no error summary");
    }

    [Fact]
    public void Falls_back_to_a_null_node_when_the_agent_is_not_in_the_map()
    {
        ToolCallTimelineMap.ToEvent(Call(agentId: Guid.NewGuid()), NodeByAgent).NodeId.ShouldBeNull();
    }
}
