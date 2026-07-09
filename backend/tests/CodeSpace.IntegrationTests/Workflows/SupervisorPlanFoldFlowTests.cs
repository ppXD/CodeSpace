using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Plans;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (the REAL <see cref="Core.Services.Supervisor.Executors.RealSupervisorActionExecutor"/> resolved from
/// DI): pins the C1 ExecutePlan OUTCOME fold — the bytes the phase projection (C2) reads and a replay must reproduce.
/// A FLAT plan records the exact pre-field <c>{planned,count}</c> outcome (the byte-identity floor); a PHASED plan
/// records its semantic phases alongside. Drives the public <c>ExecuteAsync</c> seam (ExecutePlan is reached the same
/// way the merge/resolve flow tests reach their verbs). Since triad S1 the plan verb ALSO persists the run's durable
/// <c>work_plan</c> version (a seeded team owns the row), so this suite additionally pins that write + its per-turn
/// exactly-once key — while proving the recorded OUTCOME bytes stayed untouched by the new side write.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorPlanFoldFlowTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorPlanFoldFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_flat_plan_records_the_byte_identical_outcome_and_persists_the_work_plan_exactly_once()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var context = Context(teamId);

        using var scope = _fixture.BeginScope();

        var execution = await scope.Resolve<ISupervisorActionExecutor>()
            .ExecuteAsync(PlanDecision(withPhases: false), context, CancellationToken.None);

        execution.OutcomeJson.ShouldBe("""{"planned":[{"id":"s1","title":"T","instruction":"do"}],"count":1}""",
            "a flat plan records the EXACT pre-field outcome bytes — the floor C2's projection + replay depend on; the S1 work-plan write must not touch them");

        // Triad S1: the plan verb also persisted the run's durable work_plan version.
        var db = scope.Resolve<CodeSpaceDbContext>();
        var plan = await db.WorkPlan.AsNoTracking().SingleAsync(p => p.WorkflowRunId == context.SupervisorRunId);
        plan.OriginKind.ShouldBe(WorkPlanOrigins.Supervisor);
        plan.OriginKey.ShouldBe("boss#turn0", "the per-turn exactly-once key, derived from the REAL NodeId (not the empty-NodeId fallback)");
        plan.Version.ShouldBe(1);
        JsonDocument.Parse(plan.ItemsJson).RootElement[0].GetProperty("id").GetString().ShouldBe("s1");

        // The claimed-Running crash window re-executes the SAME decision — the origin key lands it on the SAME row.
        await scope.Resolve<ISupervisorActionExecutor>().ExecuteAsync(PlanDecision(withPhases: false), context, CancellationToken.None);
        (await db.WorkPlan.AsNoTracking().CountAsync(p => p.WorkflowRunId == context.SupervisorRunId))
            .ShouldBe(1, "a re-executed plan decision must NOT duplicate the work_plan version — exactly-once per (run, turn)");
    }

    [Fact]
    public async Task An_empty_plan_is_rejected_with_a_specific_reason_and_persists_no_work_plan()
    {
        // P0-2 (action schema validation): a plan decision with zero subtasks (the model omitted the plan
        // sub-object, or bad JSON degraded it) must not silently persist an empty work_plan version — it is
        // REJECTED with a specific, actionable reason the decider reads on its next turn.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var context = Context(teamId);

        using var scope = _fixture.BeginScope();

        var execution = await scope.Resolve<ISupervisorActionExecutor>()
            .ExecuteAsync(EmptyPlanDecision(), context, CancellationToken.None);

        execution.OutcomeJson.ShouldBe("""{"plan":"rejected","reason":"the plan decision named no subtasks"}""");

        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.WorkPlan.AsNoTracking().CountAsync(p => p.WorkflowRunId == context.SupervisorRunId))
            .ShouldBe(0, "a rejected plan never reaches the work_plan persistence step");
    }

    [Fact]
    public async Task A_plan_with_an_explicit_null_subtasks_field_is_rejected_not_crashed()
    {
        // A well-formed JSON object can carry "subtasks":null explicitly (bypassing SupervisorPlanPayload's own
        // non-null default, since deserialization assigns the literal null over it) — the rejection check must
        // tolerate this exact shape, never throw a NullReferenceException on .Count.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var context = Context(teamId);

        using var scope = _fixture.BeginScope();

        var decision = new SupervisorDecision { Kind = SupervisorDecisionKinds.Plan, PayloadJson = """{"goal":"g","subtasks":null}""" };
        var execution = await scope.Resolve<ISupervisorActionExecutor>().ExecuteAsync(decision, context, CancellationToken.None);

        execution.OutcomeJson.ShouldBe("""{"plan":"rejected","reason":"the plan decision named no subtasks"}""");
    }

    [Fact]
    public async Task A_phased_plan_records_its_semantic_phases_alongside_the_subtasks()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScope();

        var execution = await scope.Resolve<ISupervisorActionExecutor>()
            .ExecuteAsync(PlanDecision(withPhases: true), Context(teamId), CancellationToken.None);

        var root = JsonDocument.Parse(execution.OutcomeJson).RootElement;
        root.GetProperty("count").GetInt32().ShouldBe(1, "planned + count are still recorded");
        root.GetProperty("planned").GetArrayLength().ShouldBe(1);

        var phases = root.GetProperty("phases");
        phases.GetArrayLength().ShouldBe(2, "the authored semantic phases are recorded in the plan outcome for the projection (C2)");
        phases[0].GetProperty("title").GetString().ShouldBe("Implement");
        phases[1].GetProperty("acceptance").GetProperty("command")[0].GetString().ShouldBe("sh", "a phase's acceptance is recorded for the projection");
    }

    private static SupervisorTurnContext Context(Guid teamId) => new()
    {
        Goal = "g",
        SupervisorRunId = Guid.NewGuid(),
        TeamId = teamId,
        NodeId = "boss",   // deliberately NOT "sup": the executor falls back to the literal "sup" on an empty NodeId, so asserting "sup#..." could not detect a broken NodeId plumb
        TurnNumber = 0,
    };

    private static SupervisorDecision EmptyPlanDecision() => new()
    {
        Kind = SupervisorDecisionKinds.Plan,
        PayloadJson = JsonSerializer.Serialize(new SupervisorPlanPayload { Goal = "g" }, AgentJson.Options),
    };

    private static SupervisorDecision PlanDecision(bool withPhases) => new()
    {
        Kind = SupervisorDecisionKinds.Plan,
        PayloadJson = JsonSerializer.Serialize(new SupervisorPlanPayload
        {
            Goal = "g",
            Subtasks = new[] { new SupervisorPlannedSubtask { Id = "s1", Title = "T", Instruction = "do" } },
            Phases = withPhases
                ? new[]
                {
                    new SupervisorPlanPhase { Id = "impl", Title = "Implement", SubtaskIds = new[] { "s1" } },
                    new SupervisorPlanPhase { Id = "verify", Title = "Verify", SubtaskIds = new[] { "s1" }, Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "sh", "check.sh" } } },
                }
                : null,
        }, AgentJson.Options),
    };
}
