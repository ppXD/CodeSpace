using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres): P2a's completion-policy stamp — BOTH run-creation seams stamp
/// <c>CompletionPolicyVersion</c> + <c>CompletionEnforcementMode</c> in the same transaction as the row, new runs
/// are Shadow (never Enforced — Lock Clause 1), and a pre-protocol row (the seeds write none) reads Legacy
/// fail-closed. This is the migration's conformance consumer: the columns exist, the producers stamp, the
/// fail-close read law holds against real stored values.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CompletionPolicyStampFlowTests
{
    private readonly PostgresFixture _fixture;

    public CompletionPolicyStampFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_trigger_seam_stamps_the_current_policy_at_creation()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            runId = await scope.Resolve<IRunStarter>().StartAsync(new RunSourceEnvelope
            {
                TeamId = teamId,
                WorkflowId = workflowId,
                WorkflowVersion = 1,
                SourceType = WorkflowRunSourceTypes.Manual,
                ActorType = WorkflowRunActorTypes.User,
                ActorId = userId,
                NormalizedPayloadJson = """{"trigger":"manual"}""",
                CreatedBy = userId,
            }, CancellationToken.None);

            await scope.Resolve<CodeSpaceDbContext>().SaveChangesAsync();
        }

        await AssertStampedAsync(runId);
    }

    [Fact]
    public async Task The_snapshot_seam_stamps_the_current_policy_at_creation()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            runId = await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(MinimalDef(), teamId, userId, "{}",
                scopeRepositoryIds: null, projectionKind: null, session: null, CancellationToken.None);
        }

        await AssertStampedAsync(runId);
    }

    [Fact]
    public async Task A_pre_protocol_row_reads_Legacy_fail_closed()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);   // seeds write rows directly — no stamp, the pre-P2a shape

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.CompletionPolicyVersion.ShouldBeNull();
        CompletionPolicy.BasisFor(run.CompletionPolicyVersion).ShouldBe(CompletionBasis.LegacyUnknown);
        CompletionPolicy.ModeFor(run.CompletionEnforcementMode).ShouldBe(CompletionEnforcementMode.Legacy);
    }

    private async Task AssertStampedAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.CompletionPolicyVersion.ShouldBe(CompletionPolicy.CurrentVersion);
        CompletionPolicy.ModeFor(run.CompletionEnforcementMode).ShouldBe(CompletionEnforcementMode.Shadow,
            "generic creation stamps Shadow — Enforced is only ever P2b's qualified-cohort write (Lock Clause 1)");
        CompletionPolicy.BasisFor(run.CompletionPolicyVersion).ShouldBe(CompletionBasis.ContractDerived);
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<MediatR.IMediator>().Send(new CodeSpace.Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "policy-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<CodeSpace.Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private static WorkflowDefinition MinimalDef() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "end" } },
    };
}
