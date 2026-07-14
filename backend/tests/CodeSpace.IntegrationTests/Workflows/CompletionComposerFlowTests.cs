using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres): the completion composer's first live chain (P2a-3) — a real tape (plan with
/// decision-bound ref → terminal spawn with a folded grade → terminal stop) + a durable requirement row compose
/// through adapter → write-through receipts → admission → operational selector → the pure reducer, and the
/// verdict lands honestly. Pins: the graded fold becomes a durable receipt EXACTLY-ONCE across re-composes; a
/// pre-protocol run projects LegacyUnknown and never re-derives; nothing here ever touches WorkflowRunStatus
/// (compute + record only — Lock Clause 1).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CompletionComposerFlowTests
{
    private readonly PostgresFixture _fixture;

    public CompletionComposerFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_graded_tape_composes_to_an_honest_assessment_with_exactly_once_receipts()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, userId, stampPolicy: true, WorkflowRunStatus.Success);
        var planId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();

        await SeedDecisionAsync(runId, teamId, 1, SupervisorDecisionKinds.Plan,
            """{"subtasks":[{"id":"s1","title":"T","instruction":"fix it"}]}""",
            $$"""{"planned":[],"count":1,"workPlanId":"{{planId}}","workPlanVersion":1}""");
        await SeedDecisionAsync(runId, teamId, 2, SupervisorDecisionKinds.Spawn,
            """{"subtaskIds":["s1"]}""",
            JsonSerializer.Serialize(new { agentResults = new[] { new { agentRunId = attemptId, status = "Succeeded", acceptancePassed = false, acceptanceDetail = "tests-failed-exit-1", acceptanceEvidenceId = (Guid?)EvidenceId, producedBranch = "codespace/agent/s1" } } }));
        await SeedDecisionAsync(runId, teamId, 3, SupervisorDecisionKinds.Stop, "{}", "{}");

        using var scope = _fixture.BeginScope();
        var store = scope.Resolve<ICompletionContractStore>();
        await store.UpsertRequirementsAsync(runId, teamId, new[]
        {
            new RequirementEnvelope { RequirementRef = "acceptance:s1", Kind = ContractKinds.Acceptance, Requiredness = Requiredness.Required, Authority = ContractAuthority.ModelProposal, ContractSchemaVersion = "1" },
        }, CancellationToken.None);

        var composer = scope.Resolve<ICompletionAssessmentComposer>();

        var first = await composer.ComposeAsync(runId, teamId, CancellationToken.None);

        first.ShouldNotBeNull();
        first!.Mode.ShouldBe(CompletionEnforcementMode.Shadow);
        first.Assessment.Basis.ShouldBe(CompletionBasis.ContractDerived);
        first.Assessment.Verification.ShouldBe(VerificationDisposition.Failed, "the folded grade reached the reducer through the full chain");
        first.Assessment.Outcome.ShouldBe(OutcomeDisposition.Unsolved, "an engine-Success run with a FAILED oracle reads honestly Unsolved");
        first.Assessment.Execution.ShouldBe(ExecutionDisposition.Completed);
        first.Rejections.ShouldBeEmpty();
        first.ContractErrors.ShouldBeEmpty();

        var second = await composer.ComposeAsync(runId, teamId, CancellationToken.None);
        second!.Assessment.ShouldBe(first.Assessment, "same contract + same facts ⇒ same assessment");

        (await store.ListReceiptsAsync(runId, teamId, CancellationToken.None)).Count
            .ShouldBe(1, "the write-through bridge is exactly-once — a re-compose lands on the first row");

        (await ScopeRunStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Success, "compute + record ONLY — the composer never touches the terminal (Lock Clause 1)");

        // P3a-1: the fold's evidence id rode the bridge onto the receipt — EvidenceRef is a fact, not prose.
        (await store.ListReceiptsAsync(runId, teamId, CancellationToken.None)).Single().EvidenceRef.ShouldBe(EvidenceId);
    }

    private static readonly Guid EvidenceId = Guid.NewGuid();

    [Fact]
    public async Task A_pre_protocol_run_projects_LegacyUnknown_and_derives_nothing()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, userId, stampPolicy: false, WorkflowRunStatus.Success);

        using var scope = _fixture.BeginScope();
        var composed = await scope.Resolve<ICompletionAssessmentComposer>().ComposeAsync(runId, teamId, CancellationToken.None);

        composed.ShouldNotBeNull();
        composed!.Mode.ShouldBe(CompletionEnforcementMode.Legacy);
        composed.Assessment.Basis.ShouldBe(CompletionBasis.LegacyUnknown);
        composed.Assessment.Outcome.ShouldBe(OutcomeDisposition.Unknown, "old tape is never re-derived into contract truth");

        (await scope.Resolve<ICompletionContractStore>().ListReceiptsAsync(runId, teamId, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_non_terminal_run_composes_nothing()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, userId, stampPolicy: true, WorkflowRunStatus.Running);

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<ICompletionAssessmentComposer>().ComposeAsync(runId, teamId, CancellationToken.None))
            .ShouldBeNull("an assessment is a terminal-time artifact");
    }

    [Fact]
    public async Task The_shadow_sweep_records_the_delta_append_only()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, userId, stampPolicy: true, WorkflowRunStatus.Success);
        var planId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();

        await SeedDecisionAsync(runId, teamId, 1, SupervisorDecisionKinds.Plan,
            """{"subtasks":[{"id":"s1","title":"T","instruction":"fix it"}]}""",
            $$"""{"planned":[],"count":1,"workPlanId":"{{planId}}","workPlanVersion":1}""");
        await SeedDecisionAsync(runId, teamId, 2, SupervisorDecisionKinds.Spawn,
            """{"subtaskIds":["s1"]}""",
            JsonSerializer.Serialize(new { agentResults = new[] { new { agentRunId = attemptId, status = "Succeeded", acceptancePassed = false, acceptanceDetail = "tests-failed-exit-1", producedBranch = "codespace/agent/s1" } } }));
        await SeedDecisionAsync(runId, teamId, 3, SupervisorDecisionKinds.Stop, "{}", "{}");

        using var scope = _fixture.BeginScope();
        await scope.Resolve<ICompletionContractStore>().UpsertRequirementsAsync(runId, teamId, new[]
        {
            new RequirementEnvelope { RequirementRef = "acceptance:s1", Kind = ContractKinds.Acceptance, Requiredness = Requiredness.Required, Authority = ContractAuthority.ModelProposal, ContractSchemaVersion = "1" },
        }, CancellationToken.None);

        var shadow = scope.Resolve<ICompletionShadowService>();

        (await shadow.SweepAsync(batchSize: 50, CancellationToken.None)).ShouldBeGreaterThanOrEqualTo(1);

        var db = scope.Resolve<CodeSpaceDbContext>();
        var record = await db.CompletionAssessmentRecord.AsNoTracking().SingleAsync(a => a.WorkflowRunId == runId);

        record.Outcome.ShouldBe("Unsolved");
        record.LegacyIsSolved.ShouldBeTrue("the legacy ladder read this engine-Success run Solved — THE degraded-inflation delta, now a standing row");
        record.EnforcementMode.ShouldBe("Shadow");
        record.Basis.ShouldBe("ContractDerived");

        // Re-sweep: the run has a record → not a candidate; even a direct re-record with an unchanged assessment appends nothing.
        (await shadow.SweepAsync(batchSize: 50, CancellationToken.None)).ShouldBe(0);
        (await db.CompletionAssessmentRecord.AsNoTracking().CountAsync(a => a.WorkflowRunId == runId)).ShouldBe(1);

        (await ScopeRunStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Success, "Shadow NEVER mutates a terminal (Lock Clause 1)");
    }

    // ── Seeds ──

    private async Task<Guid> SeedTerminalRunAsync(Guid teamId, Guid userId, bool stampPolicy, WorkflowRunStatus status)
    {
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.SingleAsync(r => r.Id == runId);
        run.Status = status;
        if (stampPolicy)
        {
            run.CompletionPolicyVersion = CompletionPolicy.CurrentVersion;
            run.CompletionEnforcementMode = CompletionPolicy.CurrentMode.ToString();
        }
        await db.SaveChangesAsync();
        return runId;
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, CodeSpace.Messages.Constants.Roles.Admin);
        return await scope.Resolve<MediatR.IMediator>().Send(new CodeSpace.Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "composer-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<CodeSpace.Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task SeedDecisionAsync(Guid runId, Guid teamId, int sequence, string kind, string payloadJson, string outcomeJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, Sequence = sequence,
            DecisionKind = kind, IdempotencyKey = $"{kind}-{Guid.NewGuid():N}", InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payloadJson, OutcomeJson = outcomeJson,
            FenceEpoch = 1, CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    private async Task<WorkflowRunStatus> ScopeRunStatusAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return (await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status;
    }
}
