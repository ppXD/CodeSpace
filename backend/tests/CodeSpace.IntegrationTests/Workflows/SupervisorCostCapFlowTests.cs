using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 SOTA #4 cost-cap enforcement crown jewel (high fidelity — REAL engine + <see cref="SupervisorTurnService"/> +
/// real spawn executor + real <see cref="AgentRunService"/> + real barrier over real Postgres; the scripted decider
/// stands in for the LLM). This is the END-TO-END proof that the once-dead <c>TokenUsage</c> is now LOAD-BEARING:
/// captured at agent completion → priced → folded onto the durable spawn outcome → summed on rehydrate → compared to
/// <c>maxCostUsd</c> → force-STOPS the next spawn. The discriminator: on main (no cost bound) this run would keep
/// spawning to the count cap; here it force-stops on COST first.
/// <list type="bullet">
///   <item>OVER-CAP: priceable agents whose realized spend exceeds <c>maxCostUsd</c> force-STOP the run CLEANLY with
///         the distinct "cost cap reached" reason — and no further agent is spawned (realized-spend backpressure).</item>
///   <item>FAIL-OPEN: an UNKNOWN-model wave (unpriceable spend) NEVER trips the cost cap, so the count cap is what
///         bounds the run — proving cost-unknown contributes 0 rather than blocking (the locked fail-open policy).</item>
/// </list>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SupervisorCostCapFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly string? _flagBefore;

    public SupervisorCostCapFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _flagBefore = Environment.GetEnvironmentVariable(SupervisorLane.EnabledEnvVar);
        Environment.SetEnvironmentVariable(SupervisorLane.EnabledEnvVar, "1");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SupervisorLane.EnabledEnvVar, _flagBefore);
        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();
        scope.Resolve<InMemoryBackgroundJobClient>().AutoExecute = true;
    }

    [Fact]
    public async Task Realized_spend_over_the_cost_cap_force_stops_the_run_and_spawns_no_more_agents()
    {
        SetScript(s => s.PlanThenSpawnForever());

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        // Tiny $0.01 budget; high spawn cap so it is unambiguously the COST cap that trips. The spawned agents run on
        // a priceable opus model (via the agentProfile) — credentialed so the option-B dispatch gate resolves it (else
        // the spawn would fail on the model gate before the cost logic), so their realized spend is real USD the bound reads.
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-opus-4-8");
        var config = """{"goal":"ship it","maxCostUsd":0.01,"maxTotalSpawns":50,"agentProfile":{"model":"claude-opus-4-8"}}""";
        var workflowId = await CreateWorkflowAsync(teamId, userId, config);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            // Turn 0 plan → self-advance. Turn 1 spawn(2) → stages 2 opus agents + parks on the barrier.
            await RunEngineAsync(runId);
            await ResolveSelfAdvanceAsync(runId);
            await RunEngineAsync(runId);

            Guid[] agents;
            using (var verify = _fixture.BeginScope())
            {
                agents = await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking()
                    .Where(r => r.WorkflowRunId == runId).Select(r => r.Id).ToArrayAsync();
                agents.Length.ShouldBe(2, "turn 1 spawned 2 agents");
            }

            // Each agent reports 1M opus input tokens → $5 each → realized run spend $10, far over the $0.01 cap.
            foreach (var agent in agents) await CompleteWithSpendAsync(agent, inputTokens: 1_000_000, outputTokens: 0);

            // The barrier resumes → rehydrate folds the priced spend onto the spawn outcome → turn 2's spawn sees
            // RunSpendUsd $10 > $0.01 → force-STOP on COST (before any third agent is created).
            await RunEngineAsync(runId);

            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();

                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                    .ShouldBe(WorkflowRunStatus.Success, "the cost cap force-STOPS the run CLEANLY (a terminal stop, not a crash)");

                (await db.AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == runId))
                    .ShouldBe(2, "realized-spend backpressure held — the over-budget turn-2 spawn was refused, no extra agents");

                var rows = await Ledger(db, runId, teamId);
                rows.Count(r => r.DecisionKind == SupervisorDecisionKinds.Spawn).ShouldBe(1, "exactly one spawn executed — the second was cost-stopped before executing");
                rows.Single(r => r.DecisionKind == SupervisorDecisionKinds.Stop).PayloadJson
                    .ShouldContain(SupervisorStopReasons.CostCapReached, customMessage: "the forced stop records the DISTINCT cost-cap reason — inspect the supervisor_decision_record stop payload");
            }
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    [Fact]
    public async Task An_unknown_model_wave_never_trips_the_cost_cap_so_the_count_cap_bounds_the_run()
    {
        SetScript(s => s.PlanThenSpawnForever());

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        // A tiny cost cap AND a tight spawn cap, but NO model on the profile → spawned agents are unpriceable. Their
        // (huge) spend is UNKNOWN → contributes 0 → the cost cap never trips; the count cap (3) is what stops the run.
        var config = """{"goal":"ship it","maxCostUsd":0.01,"maxTotalSpawns":3}""";
        var workflowId = await CreateWorkflowAsync(teamId, userId, config);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = false;

        try
        {
            await RunEngineAsync(runId);
            await ResolveSelfAdvanceAsync(runId);
            await RunEngineAsync(runId);

            Guid[] agents;
            using (var verify = _fixture.BeginScope())
            {
                agents = await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking()
                    .Where(r => r.WorkflowRunId == runId).Select(r => r.Id).ToArrayAsync();
                agents.Length.ShouldBe(2, "turn 1 spawned 2 — within the count cap of 3");
            }

            // Big spend, but UNKNOWN model → unpriceable → $0 contribution (fail-open).
            foreach (var agent in agents) await CompleteWithSpendAsync(agent, inputTokens: 9_000_000, outputTokens: 9_000_000);

            await RunEngineAsync(runId);

            using (var verify = _fixture.BeginScope())
            {
                var db = verify.Resolve<CodeSpaceDbContext>();

                (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                    .ShouldBe(WorkflowRunStatus.Success, "the run force-STOPS cleanly — but on the count cap, not cost");

                var stop = (await Ledger(db, runId, teamId)).Single(r => r.DecisionKind == SupervisorDecisionKinds.Stop);
                stop.PayloadJson.ShouldContain(SupervisorStopReasons.TotalSpawnCapReached, customMessage: "the count cap bounds the run");
                stop.PayloadJson.ShouldNotContain(SupervisorStopReasons.CostCapReached, customMessage: "cost FAILED OPEN — an unpriceable wave never blocks on cost");
            }
        }
        finally
        {
            jobClient.AutoExecute = true;
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────

    private void SetScript(Action<SupervisorDecisionScript> configure)
    {
        using var scope = _fixture.BeginScope();
        configure(scope.Resolve<SupervisorDecisionScript>());
    }

    private async Task ResolveSelfAdvanceAsync(Guid runId)
    {
        Guid waitId;
        using (var verify = _fixture.BeginScope())
        {
            waitId = (await verify.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking()
                .SingleAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.SupervisorDecision && w.Status == WorkflowWaitStatuses.Pending)).Id;
        }

        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowResumeService>().ResumeWaitAsync(runId, waitId, null, CancellationToken.None);
    }

    private async Task CompleteWithSpendAsync(Guid agentRunId, int inputTokens, int outputTokens)
    {
        using var scope = _fixture.BeginScope();
        var runs = scope.Resolve<IAgentRunService>();
        var notifier = scope.Resolve<IAgentRunCompletionNotifier>();

        await runs.MarkRunningAsync(agentRunId, CancellationToken.None);
        await runs.CompleteAsync(agentRunId, new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded,
            ExitReason = "completed",
            Summary = "done",
            TokenUsage = new AgentTokenUsage { InputTokens = inputTokens, OutputTokens = outputTokens },
        }, CancellationToken.None);
        await notifier.NotifyCompletedAsync(agentRunId, CancellationToken.None);
    }

    private static async Task<IReadOnlyList<Core.Persistence.Entities.SupervisorDecisionRecord>> Ledger(CodeSpaceDbContext db, Guid runId, Guid teamId) =>
        await db.SupervisorDecisionRecord.AsNoTracking().Where(d => d.SupervisorRunId == runId && d.TeamId == teamId).OrderBy(d => d.Sequence).ToListAsync();

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, string supervisorConfigJson)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-cost-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = SupervisorDefinition(supervisorConfigJson),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    private static WorkflowDefinition SupervisorDefinition(string supervisorConfigJson) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sup", TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json(supervisorConfigJson), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "sup" },
            new() { From = "sup", To = "end" },
        },
    };
}
