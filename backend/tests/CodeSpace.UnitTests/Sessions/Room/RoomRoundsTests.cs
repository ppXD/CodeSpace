using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions.Room;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Room;

/// <summary>
/// The pure segmentation of a supervisor decision tape into ROUNDS — a Plan and every decision up to the NEXT Plan,
/// in <c>Sequence</c> order (a re-plan opens a new round). Spawns/retries stage a round's agents; merge / resolve /
/// ask-human translate into the round's closing operation copy. No DB — the projector reads the tape and hands it here.
/// </summary>
[Trait("Category", "Unit")]
public class RoomRoundsTests
{
    private static SupervisorDecisionRecord Decision(string kind, long sequence, string? payload = null, string? outcome = null) => new()
    {
        Id = Guid.NewGuid(),
        SupervisorRunId = Guid.NewGuid(),
        TeamId = Guid.NewGuid(),
        Sequence = sequence,
        DecisionKind = kind,
        Status = SupervisorDecisionStatus.Succeeded,
        PayloadJson = payload ?? "{}",
        OutcomeJson = outcome,
        CreatedDate = DateTimeOffset.UtcNow,
    };

    private static string Plan(params string[] titles) => JsonSerializer.Serialize(new SupervisorPlanPayload
    {
        Subtasks = titles.Select((t, i) => new SupervisorPlannedSubtask { Id = $"s{i}", Title = t, Instruction = t }).ToList(),
    }, AgentJson.Options);

    private static string Staged(params Guid[] ids) => JsonSerializer.Serialize(new { agentRunIds = ids.Select(g => g.ToString()).ToArray() });

    private static string MergeClean(string branch) => JsonSerializer.Serialize(new { integration = new { status = "Clean", integratedBranch = branch } });

    [Fact]
    public void Empty_tape_yields_no_rounds()
    {
        RoomRounds.Segment(Array.Empty<SupervisorDecisionRecord>()).ShouldBeEmpty();
    }

    [Fact]
    public void A_plan_opens_one_round_carrying_its_subtask_titles()
    {
        var rounds = RoomRounds.Segment(new[] { Decision(SupervisorDecisionKinds.Plan, 1, Plan("Research", "Draft")) });

        rounds.Count.ShouldBe(1);
        rounds[0].Index.ShouldBe(1);
        rounds[0].Subtasks.ShouldBe(new[] { "Research", "Draft" });
    }

    [Fact]
    public void A_spawn_stages_its_agents_onto_the_open_round()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var rounds = RoomRounds.Segment(new[]
        {
            Decision(SupervisorDecisionKinds.Plan, 1, Plan("A", "B")),
            Decision(SupervisorDecisionKinds.Spawn, 2, outcome: Staged(a, b)),
        });

        rounds.Count.ShouldBe(1);
        rounds[0].AgentRunIds.ShouldBe(new[] { a, b });
    }

    [Fact]
    public void A_re_plan_opens_a_second_round_and_later_spawns_land_on_it()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var rounds = RoomRounds.Segment(new[]
        {
            Decision(SupervisorDecisionKinds.Plan, 1, Plan("First")),
            Decision(SupervisorDecisionKinds.Spawn, 2, outcome: Staged(first)),
            Decision(SupervisorDecisionKinds.Plan, 3, Plan("Second")),
            Decision(SupervisorDecisionKinds.Spawn, 4, outcome: Staged(second)),
        });

        rounds.Count.ShouldBe(2);
        rounds[0].Subtasks.ShouldBe(new[] { "First" });
        rounds[0].AgentRunIds.ShouldBe(new[] { first });
        rounds[1].Index.ShouldBe(2);
        rounds[1].Subtasks.ShouldBe(new[] { "Second" });
        rounds[1].AgentRunIds.ShouldBe(new[] { second });
    }

    [Fact]
    public void A_spawn_before_any_plan_opens_a_defensive_first_round()
    {
        var a = Guid.NewGuid();

        var rounds = RoomRounds.Segment(new[] { Decision(SupervisorDecisionKinds.Spawn, 1, outcome: Staged(a)) });

        rounds.Count.ShouldBe(1);
        rounds[0].Index.ShouldBe(1);
        rounds[0].Subtasks.ShouldBeEmpty();
        rounds[0].AgentRunIds.ShouldBe(new[] { a });
    }

    [Fact]
    public void A_clean_merge_becomes_the_rounds_closing_operation()
    {
        var rounds = RoomRounds.Segment(new[]
        {
            Decision(SupervisorDecisionKinds.Plan, 1, Plan("A")),
            Decision(SupervisorDecisionKinds.Merge, 2, outcome: MergeClean("integration/work")),
        });

        rounds[0].Operation.ShouldNotBeNull();
        rounds[0].Operation!.Kind.ShouldBe("merge");
        rounds[0].Operation!.Text.ShouldBe("Merged results into integration/work");
        rounds[0].Operation!.Tone.ShouldBe(NarrativeTone.Success);
    }

    [Theory]
    [InlineData(SupervisorDecisionKinds.Plan)]
    [InlineData(SupervisorDecisionKinds.Spawn)]
    [InlineData(SupervisorDecisionKinds.Retry)]
    [InlineData(SupervisorDecisionKinds.Stop)]
    public void Plan_spawn_retry_and_stop_have_no_round_operation(string kind)
    {
        RoomRounds.OperationFor(Decision(kind, 1)).ShouldBeNull();
    }
}
