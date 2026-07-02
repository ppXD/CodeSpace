using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Plans;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Plans;
using Shouldly;

namespace CodeSpace.UnitTests.Plans;

/// <summary>
/// The checklist read model's SHARED derivation rule (triad S2a) — the one place every projection maps a
/// latest attempt onto a checkable state — plus the question noun's persisted-bytes stability.
/// </summary>
[Trait("Category", "Unit")]
public class WorkPlanChecklistTests
{
    [Theory]
    [InlineData(null, null, WorkPlanItemStates.Pending)]           // never staged
    [InlineData("Queued", null, WorkPlanItemStates.InProgress)]
    [InlineData("Running", null, WorkPlanItemStates.InProgress)]
    [InlineData("Succeeded", null, WorkPlanItemStates.Completed)]  // ungraded success counts as done
    [InlineData("Succeeded", true, WorkPlanItemStates.Completed)]
    [InlineData("Succeeded", false, WorkPlanItemStates.Failed)]    // acceptance-rejected is NOT done
    [InlineData("NeedsReview", null, WorkPlanItemStates.NeedsReview)]  // finished-awaiting-a-human is NOT "it broke"
    [InlineData("Failed", null, WorkPlanItemStates.Failed)]
    [InlineData("TimedOut", null, WorkPlanItemStates.Failed)]
    [InlineData("Cancelled", null, WorkPlanItemStates.Failed)]
    [InlineData("Unknown", null, WorkPlanItemStates.Failed)]       // the fold's unresolvable-id placeholder fails closed
    public void The_state_rule_maps_the_latest_attempt(string? agentStatus, bool? acceptancePassed, string expected)
    {
        WorkPlanItemStates.Derive(agentStatus, acceptancePassed).ShouldBe(expected);
    }

    [Fact]
    public void Every_agent_run_status_has_a_deliberate_state_mapping()
    {
        // Exhaustiveness trip-wire: when AgentRunStatus grows, this forces a DELIBERATE Derive mapping — a new
        // non-terminal status silently rendering as Failed would lie about a live item.
        var deliberate = new Dictionary<string, string>
        {
            [nameof(AgentRunStatus.Queued)] = WorkPlanItemStates.InProgress,
            [nameof(AgentRunStatus.Running)] = WorkPlanItemStates.InProgress,
            [nameof(AgentRunStatus.Succeeded)] = WorkPlanItemStates.Completed,
            [nameof(AgentRunStatus.NeedsReview)] = WorkPlanItemStates.NeedsReview,
            [nameof(AgentRunStatus.Failed)] = WorkPlanItemStates.Failed,
            [nameof(AgentRunStatus.Cancelled)] = WorkPlanItemStates.Failed,
            [nameof(AgentRunStatus.TimedOut)] = WorkPlanItemStates.Failed,
        };

        foreach (var status in Enum.GetNames<AgentRunStatus>())
        {
            deliberate.ShouldContainKey(status, $"AgentRunStatus.{status} has no deliberate checklist mapping — add it here AND to WorkPlanItemStates.Derive");
            WorkPlanItemStates.Derive(status, acceptancePassed: null).ShouldBe(deliberate[status]);
        }
    }

    // ─── The pure latest-attempt fold (the projection's core join, DB-free) ───

    private static readonly Guid Agent1 = Guid.NewGuid();
    private static readonly Guid Agent2 = Guid.NewGuid();
    private static readonly Guid Agent3 = Guid.NewGuid();

    [Fact]
    public void A_folded_spawn_joins_items_to_agents_positionally()
    {
        var fold = WorkPlanChecklistService.FoldAttempts(new[]
        {
            Spawn(new[] { "a", "b" }, staged: new[] { Agent1, Agent2 }, folded: new[] { Folded(Agent1, "Succeeded", true), Folded(Agent2, "Failed", null) }),
        }, Live());

        fold["a"].AgentRunId.ShouldBe(Agent1);
        fold["a"].LatestStatus.ShouldBe("Succeeded");
        fold["a"].AcceptancePassed.ShouldBe(true);
        fold["b"].AgentRunId.ShouldBe(Agent2, "the join is POSITIONAL — a transposed join is the projection's worst defect");
        fold["b"].LatestStatus.ShouldBe("Failed");
    }

    [Fact]
    public void A_later_retry_supersedes_the_items_state_and_accumulates_attempts()
    {
        var fold = WorkPlanChecklistService.FoldAttempts(new[]
        {
            Spawn(new[] { "a", "b" }, staged: new[] { Agent1, Agent2 }, folded: new[] { Folded(Agent1, "Succeeded", null), Folded(Agent2, "Failed", null) }),
            Retry("b", staged: Agent3, folded: Folded(Agent3, "Succeeded", true)),
        }, Live());

        fold["b"].AgentRunId.ShouldBe(Agent3, "the retry's agent supersedes the failed attempt");
        fold["b"].LatestStatus.ShouldBe("Succeeded");
        fold["b"].Count.ShouldBe(2, "attempts accumulate across spawn + retry");
        fold["a"].Count.ShouldBe(1);
    }

    [Fact]
    public void A_failed_staging_contributes_nothing_and_keeps_the_prior_state()
    {
        var fold = WorkPlanChecklistService.FoldAttempts(new[]
        {
            Spawn(new[] { "a" }, staged: new[] { Agent1 }, folded: new[] { Folded(Agent1, "Failed", null) }),
            new SupervisorTapeDecision("retry", """{"subtaskId":"a"}""", OutcomeJson: null),   // terminalized-Failed staging: no outcome
        }, Live());

        fold["a"].AgentRunId.ShouldBe(Agent1, "a zero-staged decision must not disturb the prior attempt");
        fold["a"].Count.ShouldBe(1);
    }

    [Fact]
    public void An_unfolded_wave_reads_the_live_status_and_a_missing_live_row_stays_pending()
    {
        var fold = WorkPlanChecklistService.FoldAttempts(new[]
        {
            Spawn(new[] { "a", "b" }, staged: new[] { Agent1, Agent2 }, folded: Array.Empty<System.Text.Json.Nodes.JsonObject>()),
        }, Live((Agent1, "Running")));

        fold["a"].LatestStatus.ShouldBe("Running", "an in-flight wave reads the LIVE agent status");
        WorkPlanItemStates.Derive(fold["b"].LatestStatus, null).ShouldBe(WorkPlanItemStates.Pending, "a staged id with no live row derives Pending, never a lie");
    }

    // ─── Tape-row builders (the exact JSON keys the SupervisorOutcome readers consume) ───

    private static SupervisorTapeDecision Spawn(string[] subtaskIds, Guid[] staged, System.Text.Json.Nodes.JsonObject[] folded)
    {
        var payload = JsonSerializer.Serialize(new SupervisorSpawnPayload { SubtaskIds = subtaskIds }, AgentJson.Options);
        var outcome = folded.Length > 0
            ? JsonSerializer.Serialize(new { agentRunIds = staged, agentResults = folded }, AgentJson.Options)
            : JsonSerializer.Serialize(new { agentRunIds = staged }, AgentJson.Options);
        return new SupervisorTapeDecision("spawn", payload, outcome);
    }

    private static SupervisorTapeDecision Retry(string subtaskId, Guid staged, System.Text.Json.Nodes.JsonObject folded)
    {
        var payload = JsonSerializer.Serialize(new SupervisorRetryPayload { SubtaskId = subtaskId }, AgentJson.Options);
        var outcome = JsonSerializer.Serialize(new { agentRunIds = new[] { staged }, agentResults = new[] { folded } }, AgentJson.Options);
        return new SupervisorTapeDecision("retry", payload, outcome);
    }

    private static System.Text.Json.Nodes.JsonObject Folded(Guid agentRunId, string status, bool? acceptancePassed)
    {
        var o = new System.Text.Json.Nodes.JsonObject { ["agentRunId"] = agentRunId, ["status"] = status };
        if (acceptancePassed is { } p) o["acceptancePassed"] = p;
        return o;
    }

    private static IReadOnlyDictionary<Guid, string> Live(params (Guid Id, string Status)[] rows) => rows.ToDictionary(r => r.Id, r => r.Status);

    [Fact]
    public void A_minimal_question_serializes_without_optional_keys()
    {
        var json = JsonSerializer.Serialize(new WorkPlanQuestion
        {
            Id = "q1",
            Question = "Which direction?",
            Options = new[] { new WorkPlanQuestionOption { Id = "a", Label = "Fast" }, new WorkPlanQuestionOption { Id = "b", Label = "Thorough" } },
        }, AgentJson.Options);

        json.ShouldBe("""{"id":"q1","question":"Which direction?","options":[{"id":"a","label":"Fast"},{"id":"b","label":"Thorough"}],"allowFreeText":false}""");
    }

    // ── S5: the map#i branch-index parse the plan-map checklist join rides ──

    [Theory]
    [InlineData("map#0", true, 0)]
    [InlineData("map#7", true, 7)]
    [InlineData("map#-1", false, 0)]                 // a negative ordinal is nonsense
    [InlineData("outer#0/map#1", false, 0)]          // a NESTED branch key must not join top-level items
    [InlineData("fanout#2", false, 0)]               // a differently-id'd map stays honestly Pending
    [InlineData("", false, 0)]
    public void Branch_index_parses_only_top_level_map_keys(string key, bool ok, int expected)
    {
        CodeSpace.Core.Services.Plans.WorkPlanChecklistService.TryParseBranchIndex(key, out var index).ShouldBe(ok);

        if (ok) index.ShouldBe(expected);
    }
}
