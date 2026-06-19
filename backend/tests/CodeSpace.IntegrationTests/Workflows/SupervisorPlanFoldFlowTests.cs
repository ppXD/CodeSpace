using System.Text.Json;
using Autofac;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (the REAL <see cref="Core.Services.Supervisor.Executors.RealSupervisorActionExecutor"/> resolved from
/// DI): pins the C1 ExecutePlan OUTCOME fold — the bytes the phase projection (C2) reads and a replay must reproduce.
/// A FLAT plan records the exact pre-field <c>{planned,count}</c> outcome (the byte-identity floor); a PHASED plan
/// records its semantic phases alongside. Drives the public <c>ExecuteAsync</c> seam (ExecutePlan is reached the same
/// way the merge/resolve flow tests reach their verbs) — no DB seed needed, ExecutePlan only reads the decision payload.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorPlanFoldFlowTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorPlanFoldFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_flat_plan_records_the_byte_identical_outcome()
    {
        using var scope = _fixture.BeginScope();

        var execution = await scope.Resolve<ISupervisorActionExecutor>()
            .ExecuteAsync(PlanDecision(withPhases: false), Context(), CancellationToken.None);

        execution.OutcomeJson.ShouldBe("""{"planned":[{"id":"s1","title":"T","instruction":"do"}],"count":1}""",
            "a flat plan records the EXACT pre-field outcome bytes — the floor C2's projection + replay depend on");
    }

    [Fact]
    public async Task A_phased_plan_records_its_semantic_phases_alongside_the_subtasks()
    {
        using var scope = _fixture.BeginScope();

        var execution = await scope.Resolve<ISupervisorActionExecutor>()
            .ExecuteAsync(PlanDecision(withPhases: true), Context(), CancellationToken.None);

        var root = JsonDocument.Parse(execution.OutcomeJson).RootElement;
        root.GetProperty("count").GetInt32().ShouldBe(1, "planned + count are still recorded");
        root.GetProperty("planned").GetArrayLength().ShouldBe(1);

        var phases = root.GetProperty("phases");
        phases.GetArrayLength().ShouldBe(2, "the authored semantic phases are recorded in the plan outcome for the projection (C2)");
        phases[0].GetProperty("title").GetString().ShouldBe("Implement");
        phases[1].GetProperty("acceptance").GetProperty("command")[0].GetString().ShouldBe("sh", "a phase's acceptance is recorded for the projection");
    }

    private static SupervisorTurnContext Context() => new()
    {
        Goal = "g",
        SupervisorRunId = Guid.NewGuid(),
        TeamId = Guid.NewGuid(),
        NodeId = "sup",
        TurnNumber = 0,
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
