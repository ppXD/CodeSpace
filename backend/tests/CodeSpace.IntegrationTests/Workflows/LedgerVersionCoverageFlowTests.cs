using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres, the REAL writers from DI): P2's ledger-version FULL coverage — every write that
/// changes the completion composer's read set moves <c>completion_ledger_head.version</c>, so the terminal CAS
/// (<c>TryStampArbitratedTerminalAsync</c>) refuses any stamp arbitrated over facts that moved underneath it.
/// Covered here: an agent run's terminal completion, a supervisor decision turning terminal, a folded outcome
/// enrichment (idempotent no-op stays version-quiet), and the per-unit verdict stamp (the B-pre write-back, which
/// shipped without a bump). The manifest upsert and contract-store writes were already covered by earlier slices.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class LedgerVersionCoverageFlowTests
{
    private readonly PostgresFixture _fixture;

    public LedgerVersionCoverageFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_agent_runs_terminal_completion_moves_the_version()
    {
        var (runId, teamId) = await SeedRunAsync();
        var agentRunId = await SeedRunningAgentRunAsync(teamId, runId);

        using var scope = _fixture.BeginScope();
        await scope.Resolve<IAgentRunService>().CompleteAsync(agentRunId, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = "done" }, CancellationToken.None);

        (await HeadVersionAsync(runId)).ShouldBe(1, "the terminal result is in the composer's read set — a completion racing a compose must make the CAS refuse");
    }

    [Fact]
    public async Task A_decision_turning_terminal_and_a_folded_outcome_move_the_version()
    {
        var (runId, teamId) = await SeedRunAsync();

        using var scope = _fixture.BeginScope();
        var log = scope.Resolve<ISupervisorDecisionLog>();

        var claim = await log.TryClaimAsync(runId, teamId, SupervisorDecisionKinds.Plan, "k1", "h", "{}", fenceEpoch: 1, CancellationToken.None);
        (await log.TryBeginExecutionAsync(claim.DecisionId, teamId, CancellationToken.None)).ShouldBeTrue();

        await log.RecordTerminalAsync(claim.DecisionId, teamId, SupervisorDecisionStatus.Succeeded, "{}", null, CancellationToken.None);
        (await HeadVersionAsync(runId)).ShouldBe(1, "a decision entering the terminal read set moves the version");

        await log.UpdateOutcomeAsync(claim.DecisionId, teamId, """{"folded":true}""", CancellationToken.None);
        (await HeadVersionAsync(runId)).ShouldBe(2, "a folded outcome changes the bytes the composer reads");

        await log.UpdateOutcomeAsync(claim.DecisionId, teamId, """{"folded":true}""", CancellationToken.None);
        (await HeadVersionAsync(runId)).ShouldBe(2, "an idempotent re-fold is byte-identical — version-quiet, or every rehydrate would park racing stamps for nothing");
    }

    [Fact]
    public async Task The_per_unit_verdict_stamp_moves_the_version()
    {
        var (runId, teamId) = await SeedRunAsync();
        var agentRunId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var store = scope.Resolve<IPublishManifestStore>();

        await store.UpsertForAgentRunAsync(agentRunId, new PublishManifestUpsert
        {
            TeamId = teamId, WorkflowRunId = runId, RepositoryAlias = "primary", RepositoryId = Guid.NewGuid(),
            Branch = "codespace/agent/x", CommitSha = "fee1dead", ChangedFileCount = 1, PublishStateValue = PublishState.Pushed,
        }, CancellationToken.None);

        var afterUpsert = await HeadVersionAsync(runId);

        await store.StampAcceptanceForAgentRunAsync(agentRunId, PublishAcceptanceState.Passed, CancellationToken.None);

        (await HeadVersionAsync(runId)).ShouldBe(afterUpsert + 1, "the fold's verdict write-back changes the scorecard's oracle read — the B-pre gap this slice closes");

        await store.StampAcceptanceForAgentRunAsync(Guid.NewGuid(), PublishAcceptanceState.Passed, CancellationToken.None);
        (await HeadVersionAsync(runId)).ShouldBe(afterUpsert + 1, "a stamp that touched no rows is version-quiet");
    }

    private async Task<long> HeadVersionAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().CompletionLedgerHead.AsNoTracking()
            .Where(h => h.WorkflowRunId == runId).Select(h => h.Version).FirstOrDefaultAsync();
    }

    private async Task<(Guid RunId, Guid TeamId)> SeedRunAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, CodeSpace.Messages.Constants.Roles.Admin))
        {
            workflowId = await scope.Resolve<MediatR.IMediator>().Send(new CodeSpace.Messages.Commands.Workflows.CreateWorkflowCommand
            {
                Name = "ledgercov-" + Guid.NewGuid().ToString("N")[..8],
                Description = null,
                Definition = WorkflowsTestSeed.MinimalDefinition(),
                Activations = new List<CodeSpace.Messages.Commands.Workflows.WorkflowActivationInput>(),
                Enabled = true,
            });
        }

        return (await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId), teamId);
    }

    private async Task<Guid> SeedRunningAgentRunAsync(Guid teamId, Guid workflowRunId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var run = new AgentRun { Id = Guid.NewGuid(), TeamId = teamId, WorkflowRunId = workflowRunId, Harness = "codex-cli", Status = AgentRunStatus.Running, TaskJson = "{}" };
        db.AgentRun.Add(run);
        await db.SaveChangesAsync();

        return run.Id;
    }
}
