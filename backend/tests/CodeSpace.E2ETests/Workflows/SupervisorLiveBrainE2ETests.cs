using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// Proves the LIVE-BRAIN decider swap (P0-b.2): flipping <see cref="SupervisorDeciderMode.UseLiveModel"/> makes the
/// REAL engine + turn service resolve the PRODUCTION <see cref="CodeSpace.Core.Services.Supervisor.Deciders.LlmSupervisorDecider"/>
/// instead of the <see cref="ScriptedSupervisorDecider"/> — the seam a model-driven whole-loop run rides. This test
/// proves the swap WITHOUT a key by driving the live decider's FAIL-CLOSED path: with the flag on but NO
/// <c>supervisorModelId</c> authored, the live decider returns its deterministic "no brain model" terminal stop
/// (which the scripted decider can NEVER produce — it always plans then stops with "completed"), so a single
/// <c>stop</c> whose outcome is <c>"no-model"</c> is the fingerprint that the LIVE decider really ran through the real
/// engine. The keyed whole-loop (live brain → real agents → verified patch) is the follow-up that reuses this exact seam.
///
/// <para>Fidelity: real engine + real Postgres + the real <see cref="SupervisorTurnService"/> resolving the real
/// <c>LlmSupervisorDecider</c> via DI — only the model CALL is never reached (the fail-closed guard fires first), so no
/// key/network is needed and this runs in the normal Engine-surface lane.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class SupervisorLiveBrainE2ETests : IDisposable
{
    private const string NodeId = "sup";

    private readonly PostgresFixture _fixture;
    private readonly string? _laneBefore;

    public SupervisorLiveBrainE2ETests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _laneBefore = Environment.GetEnvironmentVariable(SupervisorLane.EnabledEnvVar);
        Environment.SetEnvironmentVariable(SupervisorLane.EnabledEnvVar, "1");
        SetDeciderMode(useLiveModel: true);   // the engine now resolves the PRODUCTION LlmSupervisorDecider
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SupervisorLane.EnabledEnvVar, _laneBefore);

        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDeciderMode>().UseLiveModel = false;   // CRITICAL: restore the shared-fixture default for sibling tests
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();
        scope.Resolve<InMemoryBackgroundJobClient>().AutoExecute = true;
    }

    [Fact]
    public async Task Flipping_to_the_live_decider_makes_the_engine_resolve_it_and_fail_closed_without_a_brain_model()
    {
        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // A supervisor authored with a goal but NO supervisorModelId → the LIVE decider's fail-closed guard fires and
        // it returns a single deterministic "no-model" stop BEFORE ever calling the model. The scripted decider would
        // instead plan→stop with "completed", so the outcome below is an unambiguous fingerprint of the live decider.
        var workflowId = await CreateSupervisorWorkflowAsync(teamId, userId, """{"goal":"ship the feature"}""");
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBeOneOf(WorkflowRunStatus.Success, WorkflowRunStatus.Failure);   // it reached a TERMINAL state (the live decider drove the node to a stop), not Running

        var decisions = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId)
            .OrderBy(d => d.Sequence)
            .ToListAsync();

        var stop = decisions.ShouldHaveSingleItem();
        stop.DecisionKind.ShouldBe(SupervisorDecisionKinds.Stop,
            "with no brain model the LIVE decider fails closed to a SINGLE stop — not the scripted plan→stop arc");
        (stop.OutcomeJson + stop.PayloadJson).ShouldContain("no-model",
            customMessage: "the stop must carry the live decider's 'no-model' fail-closed fingerprint — proving the engine resolved the LlmSupervisorDecider (the scripted decider stops with 'completed', never 'no-model')");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────

    private void SetDeciderMode(bool useLiveModel)
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDeciderMode>().UseLiveModel = useLiveModel;
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private async Task<Guid> CreateSupervisorWorkflowAsync(Guid teamId, Guid userId, string supConfig)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-livebrain-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json(supConfig), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<EdgeDefinition>
                {
                    new() { From = "start", To = NodeId },
                    new() { From = NodeId, To = "end" },
                },
            },
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }
}
