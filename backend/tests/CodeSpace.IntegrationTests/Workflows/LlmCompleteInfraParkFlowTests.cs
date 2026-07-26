using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 High fidelity (real Postgres + the real <see cref="WorkflowEngine"/> + the real <c>llm.complete</c> node + the
/// production recording decorator): A2's model-plane park, proven as a DURABLE ROUND TRIP rather than as a helper
/// returning the right object.
///
/// <para><b>Why this tier is mandatory here.</b> A2's claim is that a node calling a model rides out a provider
/// outage. Unit tests over <c>InfraPark</c> can only show that the helper builds a suspension token; every link that
/// makes the claim TRUE is outside them — that the fault actually reaches the node's catch filter through the
/// decorator chain, that the engine persists the wait, that the deadline wake re-enters the SAME node, that the
/// ladder marker survives a Postgres round trip, and that the node re-runs its model call instead of replaying a
/// cached result. A single wrong link leaves the feature inert while every unit test stays green.</para>
///
/// <para><b>Where the model-plane fault comes from.</b> <see cref="FaultingLlmClient"/> throws a real
/// <see cref="LlmApiException"/> at the <see cref="ILLMClient"/> seam — one layer BELOW the only other injected
/// LLM fault in the suite, which sits at <c>ISupervisorDecider</c> and is unreachable from a <c>llm.complete</c>
/// node. Because it is registered as <c>ILLMClient</c>, Autofac wraps it in the same recording decorator production
/// uses, so these tests also prove the exception survives that decorator rather than assuming it does.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class LlmCompleteInfraParkFlowTests : IDisposable
{
    private const string Model = "faulting-model-pin";

    private readonly PostgresFixture _fixture;

    public LlmCompleteInfraParkFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    /// <summary>The fake is a ROOT singleton shared with every sibling test in the collection, so an armed script must never outlive this class.</summary>
    public void Dispose()
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<LlmFaultScript>().Reset();
    }

    [Fact]
    public async Task A_model_outage_parks_the_node_instead_of_failing_the_run()
    {
        var (runId, _) = await SeedParkableRunAsync();
        Script().FaultNext(1);

        await RunEngineAsync(runId);

        var (run, wait) = await ReadAsync(runId);

        run.Status.ShouldBe(WorkflowRunStatus.Suspended, "a provider outage is not the run's fault — before A2 this same blip terminalized the run as a Failure");
        run.Error.ShouldBeNull("a parked run has no error to show; an error here would surface an outage as a defect in the user's workflow");

        wait.ShouldNotBeNull("the park must be DURABLE — an in-memory delay would evaporate on the next deploy");
        wait!.WaitKind.ShouldBe(WorkflowWaitKinds.SupervisorInfraPark, "the same wait kind the supervisor parks on, which is what lets the stranded-wait reconciler back this park up with no new code");
        wait.NodeId.ShouldBe("gen");
        wait.WakeAt.ShouldNotBeNull("nothing but the deadline can resolve this wait — without a wake the run is parked forever");
        wait.WakeAt!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow, "the wake is in the future; a past deadline would fire immediately and spin");

        ReadParks(wait.PayloadJson).ShouldBe(1, "first rung of the ladder");
    }

    [Fact]
    public async Task The_deadline_wake_re_enters_the_node_and_the_run_finishes_once_the_outage_lifts()
    {
        // The load-bearing round trip: this is the whole point of parking rather than failing.
        var (runId, _) = await SeedParkableRunAsync();
        Script().FaultNext(1);

        await RunEngineAsync(runId);
        var (_, parked) = await ReadAsync(runId);

        await FireDeadlineAsync(parked!.Id, parked.PayloadJson!);
        await RunEngineAsync(runId);

        var (run, wait) = await ReadAsync(runId);

        run.Status.ShouldBe(WorkflowRunStatus.Success, $"the wake must re-enter node 'gen' and complete the run (run error: {run.Error ?? "(none)"}) — check workflow_run_wait for a lingering Pending row if this fails");
        wait.ShouldBeNull("the resolved park must not leave a pending wait behind");

        Script().Thrown.ShouldBe(1, "exactly one call faulted — a zero here would mean the fake never armed and this test proved nothing");
        (await ReadNodeOutputAsync(runId)).ShouldContain(FaultingLlmClient.Recovered, Case.Sensitive,
            "the node must RE-RUN its model call on the wake; this marker can only come from a fresh call, so its absence means the engine replayed a cached result instead of retrying the outage");
    }

    [Fact]
    public async Task A_second_outage_advances_the_ladder_rather_than_restarting_it()
    {
        // The marker has to survive Postgres, not just a method return: the resume payload is the ONLY carrier of
        // ladder position, so a park that re-reads it wrong waits 1m forever and never reaches its honest ending.
        var (runId, _) = await SeedParkableRunAsync();
        Script().FaultAlways();

        await RunEngineAsync(runId);
        var (_, first) = await ReadAsync(runId);

        await FireDeadlineAsync(first!.Id, first.PayloadJson!);
        await RunEngineAsync(runId);

        var (run, second) = await ReadAsync(runId);

        run.Status.ShouldBe(WorkflowRunStatus.Suspended, "the outage never lifted, so the node parks again rather than failing");
        ReadParks(second!.PayloadJson).ShouldBe(2, "the ladder position rode the resume payload through the database — a reset to 1 means the wake did not deliver the marker");

        second.WakeAt!.Value.ShouldBeGreaterThan(first.WakeAt!.Value, "the second rung waits longer than the first; equal delays would hammer a provider that is already down");
        ReadFirstParkedAt(second.PayloadJson).ShouldBe(ReadFirstParkedAt(first.PayloadJson), "the 24h window is anchored at the FIRST park, so a run can never park forever by re-anchoring");
    }

    [Fact]
    public async Task An_auth_failure_still_fails_the_node_instead_of_hiding_behind_the_ladder()
    {
        // The scope fence. Parking an operator-actionable fault is strictly worse than failing: nobody is paged, and
        // the run sits idle for a day before reporting a problem that was fixable in a minute.
        var (runId, _) = await SeedParkableRunAsync();
        Script().FaultNext(1, LlmErrorCategory.AuthFailed);

        await RunEngineAsync(runId);

        var (run, wait) = await ReadAsync(runId);

        run.Status.ShouldBe(WorkflowRunStatus.Failure, "a bad credential must surface NOW — check the node's catch filter if this parked instead");
        wait.ShouldBeNull("an auth failure must not create a park wait");
    }

    // ── Chassis ──────────────────────────────────────────────────────────────────────

    private async Task<(Guid RunId, Guid TeamId)> SeedParkableRunAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, Model, provider: FaultingLlmClient.ProviderTag);

        var workflowId = await CreateWorkflowAsync(teamId, userId);
        return (await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId), teamId);
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);

        // The model is PINNED: SeedTeamAsync also seeds a "<tag>-model" per fake provider, so an unpinned pick would
        // tie-break on a Postgres collation and could select a model this fake never sees.
        var def = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "gen", TypeKey = "llm.complete",
                        Config = WorkflowsTestSeed.Json($$"""{"provider":"{{FaultingLlmClient.ProviderTag}}","model":"{{Model}}"}"""),
                        Inputs = WorkflowsTestSeed.Json("""{"userPrompt":"summarise the outage"}""") },
                new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition> { new() { From = "start", To = "gen" }, new() { From = "gen", To = "end" } },
        };

        return await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "llm-infra-park-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = def,
            Activations = new List<Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private LlmFaultScript Script()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<LlmFaultScript>();
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    /// <summary>Fires the wake the engine scheduled — the same call the scheduled job makes, so this is a faithful simulation rather than a shortcut around the wake path.</summary>
    private async Task FireDeadlineAsync(Guid waitId, string timeoutPayloadJson)
    {
        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IWorkflowResumeService>().ResumeByDeadlineAsync(waitId, timeoutPayloadJson, CancellationToken.None))
            .ShouldBeTrue($"the deadline must resolve pending wait {waitId} — inspect workflow_run_wait manually if this fails");
    }

    private async Task<(WorkflowRun Run, WorkflowRunWait? Wait)> ReadAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        return (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId),
                await db.WorkflowRunWait.AsNoTracking().FirstOrDefaultAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending));
    }

    private async Task<string> ReadNodeOutputAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var nodes = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == runId && n.NodeId == "gen").ToListAsync();

        return string.Join(" ", nodes.Select(n => n.OutputsJson));
    }

    private static int ReadParks(string? markerJson) => JsonDocument.Parse(markerJson!).RootElement.GetProperty("parks").GetInt32();

    private static string? ReadFirstParkedAt(string? markerJson) => JsonDocument.Parse(markerJson!).RootElement.GetProperty("firstParkedAtUtc").GetString();
}
