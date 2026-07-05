using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Tasks.Timeline;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks.Timeline;

/// <summary>
/// The pure tool-call → timeline mapping: severity + level ride the CLOSED <see cref="ToolCallLedgerStatus"/> axis
/// (a failed/denied/awaiting side effect is a milestone; a landed one folds to Detail); the TITLE is outcome-aware —
/// a landed call reads the past-tense action, a non-landed one the gerund with a status qualifier, an unknown tool
/// degrades to "Called {kind}"; a landed success surfaces a legible result line; the agent + node are stamped on. No DB.
/// </summary>
[Trait("Category", "Unit")]
public class ToolCallTimelineMapTests
{
    private static readonly Guid AgentId = Guid.NewGuid();
    private static readonly IReadOnlyDictionary<Guid, string?> NodeByAgent = new Dictionary<Guid, string?> { [AgentId] = "code" };

    private static ToolCallLedger Call(ToolCallLedgerStatus status = ToolCallLedgerStatus.Succeeded, string toolKind = "git.open_pr", string? error = null, string? resultJson = null, Guid? agentId = null) => new()
    {
        Id = Guid.NewGuid(),
        AgentRunId = agentId ?? AgentId,
        ToolKind = toolKind,
        Status = status,
        Error = error,
        ResultJson = resultJson,
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
    // A side effect that DIDN'T LAND — failed, denied, an approval expired unrun, OR one still blocked on the human's
    // approval (the operator must act) — is a story milestone; a landed / in-flight call folds to Detail under its wave.
    [InlineData(ToolCallLedgerStatus.Failed, TimelineLevel.Milestone)]
    [InlineData(ToolCallLedgerStatus.Denied, TimelineLevel.Milestone)]
    [InlineData(ToolCallLedgerStatus.Expired, TimelineLevel.Milestone)]
    [InlineData(ToolCallLedgerStatus.AwaitingApproval, TimelineLevel.Milestone)]
    [InlineData(ToolCallLedgerStatus.Succeeded, TimelineLevel.Detail)]
    [InlineData(ToolCallLedgerStatus.Pending, TimelineLevel.Detail)]
    [InlineData(ToolCallLedgerStatus.Running, TimelineLevel.Detail)]
    public void A_side_effect_that_did_not_land_or_awaits_you_is_a_milestone_the_rest_fold(ToolCallLedgerStatus status, TimelineLevel expected)
    {
        ToolCallTimelineMap.ToEvent(Call(status), NodeByAgent).Level.ShouldBe(expected);
    }

    [Theory]
    // The title is OUTCOME-AWARE: a landed call names the past-tense action, a non-landed one the gerund + status.
    [InlineData(ToolCallLedgerStatus.Succeeded, "Opened a pull request")]
    [InlineData(ToolCallLedgerStatus.Failed, "Opening the pull request failed")]
    [InlineData(ToolCallLedgerStatus.Denied, "Opening the pull request was denied")]
    [InlineData(ToolCallLedgerStatus.Expired, "Opening the pull request — approval expired unrun")]
    [InlineData(ToolCallLedgerStatus.AwaitingApproval, "Opening the pull request — awaiting your approval")]
    [InlineData(ToolCallLedgerStatus.Pending, "Opening the pull request")]
    [InlineData(ToolCallLedgerStatus.Running, "Opening the pull request")]
    public void Title_is_outcome_aware_per_status(ToolCallLedgerStatus status, string expected)
    {
        ToolCallTimelineMap.ToEvent(Call(status, toolKind: "git.open_pr"), NodeByAgent).Title.ShouldBe(expected);
    }

    [Theory]
    [InlineData("git.commit", ToolCallLedgerStatus.Succeeded, "Committed the changes")]
    [InlineData("run_command", ToolCallLedgerStatus.Succeeded, "Ran a command")]
    [InlineData("run_command", ToolCallLedgerStatus.Failed, "Running the command failed")]
    [InlineData("deploy.trigger", ToolCallLedgerStatus.Denied, "Triggering the deploy was denied")]
    public void Known_tools_read_a_friendly_action(string kind, ToolCallLedgerStatus status, string expected)
    {
        ToolCallTimelineMap.ToEvent(Call(status, toolKind: kind), NodeByAgent).Title.ShouldBe(expected);
    }

    [Fact]
    public void An_unknown_tool_degrades_to_the_tool_neutral_called_kind()
    {
        // An OPEN tool kind still reads legibly — the success form is the original "Called {kind}", the non-landed form
        // its gerund — never a bare switch / dropped step.
        ToolCallTimelineMap.ToEvent(Call(ToolCallLedgerStatus.Succeeded, toolKind: "git.rebase"), NodeByAgent).Title.ShouldBe("Called git.rebase");
        ToolCallTimelineMap.ToEvent(Call(ToolCallLedgerStatus.Failed, toolKind: "git.rebase"), NodeByAgent).Title.ShouldBe("Calling git.rebase failed");
    }

    [Fact]
    public void Every_status_maps_to_a_defined_severity_and_level()
    {
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
        var ev = ToolCallTimelineMap.ToEvent(Call((ToolCallLedgerStatus)999), NodeByAgent);

        ev.Severity.ShouldBe(TimelineSeverity.Info);
        ev.Level.ShouldBe(TimelineLevel.Detail);
    }

    [Fact]
    public void Stamps_the_id_kind_agent_node_and_provenance()
    {
        var call = Call(toolKind: "git.commit");

        var ev = ToolCallTimelineMap.ToEvent(call, NodeByAgent);

        ev.Id.ShouldBe($"tool-{call.Id:N}");
        ev.Kind.ShouldBe("tool.git.commit", "the provenance kind embeds the tool kind (never switched on)");
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
    public void The_error_rides_the_summary_on_a_failure()
    {
        ToolCallTimelineMap.ToEvent(Call(ToolCallLedgerStatus.Failed, error: "remote rejected: protected branch"), NodeByAgent)
            .Summary.ShouldBe("remote rejected: protected branch");
    }

    [Fact]
    public void A_landed_success_surfaces_a_legible_result_line_when_the_tool_recorded_one()
    {
        // The recorded outcome (a PR ref) rides the summary so a success isn't a bare title — best-effort + tool-neutral.
        ToolCallTimelineMap.ToEvent(Call(ToolCallLedgerStatus.Succeeded, resultJson: """{"html_url":"https://example.com/pr/42"}"""), NodeByAgent)
            .Summary.ShouldBe("https://example.com/pr/42");

        ToolCallTimelineMap.ToEvent(Call(ToolCallLedgerStatus.Succeeded, resultJson: """{"number":42}"""), NodeByAgent)
            .Summary.ShouldBe("#42", "a PR/issue number reads as #N");

        ToolCallTimelineMap.ToEvent(Call(ToolCallLedgerStatus.Succeeded), NodeByAgent)
            .Summary.ShouldBeNull("a success with no recorded result carries no detail — never a raw blob");
    }

    [Fact]
    public void Falls_back_to_a_null_node_when_the_agent_is_not_in_the_map()
    {
        ToolCallTimelineMap.ToEvent(Call(agentId: Guid.NewGuid()), NodeByAgent).NodeId.ShouldBeNull();
    }
}
