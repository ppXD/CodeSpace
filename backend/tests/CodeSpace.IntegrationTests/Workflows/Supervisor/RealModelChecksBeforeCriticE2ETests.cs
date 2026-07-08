using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Agents.Review;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Review;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Arbiter;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// Real-model E2E for the P1d "checks before critics" ordering, wired with a LIVE reviewer over the REAL turn loop +
/// real Postgres. The point P1d makes about SAVED TOKENS is only worth proving against a real (expensive) reviewer:
/// even with a live critic credential bound to the run, a structurally-invalid PLAN (a cyclic <c>DependsOn</c>) is
/// force-stopped by the free Tier-0 validator and NEVER reaches the model — no verdict beat lands, no tokens burn
/// (GATED, and deterministic even with the live reviewer wired). The complement is REPORTED: a WELL-FORMED plan under
/// the same live critic DOES reach it (a verdict beat lands), so the pre-filter starves only doomed work of review,
/// never valid work — model-dependent, so reported not gated. Gated on <c>CODESPACE_LLM_*</c> (green-skip without it).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
public sealed class RealModelChecksBeforeCriticE2ETests
{
    private const string Custom = "Custom";
    private const string NodeId = "sup";
    private const string Goal = "ship the feature";

    private readonly PostgresFixture _fixture;

    public RealModelChecksBeforeCriticE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_doomed_plan_never_reaches_a_live_reviewer_while_a_valid_one_does()
    {
        var baseUrl = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        if (baseUrl is null || apiKey is null || model is null) return;   // secrets absent → skip

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var reviewerRowId = await SeedCredentialedModelAsync(teamId, model, baseUrl, apiKey);

        await RealModelGate.AssessLiveAsync(Custom, async () =>
        {
            // ── The doomed plan: a→b→a cycle. GATED — the free validator halts it before any model call, always. ──
            var doomedRunId = await SeedSupervisorRunAsync(teamId, userId);
            await RunPlanTurnAsync(doomedRunId, teamId, reviewerRowId, ("a", new[] { "b" }), ("b", new[] { "a" }));
            var doomed = await LedgerAsync(doomedRunId, teamId);

            var stop = doomed.Single(d => d.DecisionKind == SupervisorDecisionKinds.Stop);
            stop.PayloadJson.ShouldContain(SupervisorStopReasons.PlanInvalid, customMessage: "the free validator force-stops the cyclic plan");
            doomed.ShouldAllBe(d => SupervisorOutcome.ReadReviews(d.OutcomeJson).Count == 0,
                "P1d: with a LIVE reviewer bound, the doomed plan STILL burns zero review beats — the deterministic check gates first");

            // ── The valid plan: a well-formed DAG. REPORTED — did it actually reach the live reviewer? ──
            var validRunId = await SeedSupervisorRunAsync(teamId, userId);
            await RunPlanTurnAsync(validRunId, teamId, reviewerRowId, ("a", null), ("b", new[] { "a" }));
            var valid = await LedgerAsync(validRunId, teamId);

            // The plan may be recorded (approved), or escalate to ask_human (a live disapproval) — either way it PASSED the
            // pre-filter into real review. A gateway blip means fail-open (no beat) — so the reviewed-count is REPORTED.
            var reviewedBeats = valid.Sum(d => SupervisorOutcome.ReadReviews(d.OutcomeJson).Count);
            var reachedReviewer = reviewedBeats > 0;

            return (true,   // the GATED half already asserted above; this returns the REPORT on the valid side
                $"doomed: {stop.PayloadJson.Length}b stop, 0 review beats (gated ✓) · " +
                $"valid: {valid.Count} decisions, {reviewedBeats} live review beats — reached-live-reviewer={reachedReviewer} (reported)");
        });
    }

    // ─── Helpers ───

    private async Task RunPlanTurnAsync(Guid runId, Guid teamId, Guid reviewerRowId, params (string Id, string[]? DependsOn)[] subtasks)
    {
        using var scope = _fixture.BeginScope();

        var liveCritic = new LlmStructuredCritic(RealModelLiveWire.Registry(), scope.Resolve<IModelPoolSelector>());
        var decider = new CriticSupervisorDeciderDecorator(new PlanDecider(subtasks), liveCritic, new NoAgentPlanReviewer());

        var service = new SupervisorTurnService(
            scope.Resolve<ISupervisorDecisionLog>(),
            decider,
            scope.Resolve<ISupervisorActionExecutor>(),
            scope.Resolve<CodeSpaceDbContext>(),
            scope.Resolve<ISupervisorAcceptanceGrader>(),
            scope.Resolve<IDecisionQueueService>(),
            scope.Resolve<IDecisionArbiter>(),
            scope.Resolve<IDecisionAnswerService>(),
            scope.Resolve<CodeSpace.Core.Services.Plans.IWorkPlanService>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Lifecycle.IRunRecordLogger>(), scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(), scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IPublishManifestStore>(), scope.Resolve<ILogger<SupervisorTurnService>>());

        var goalConfig = new SupervisorGoalConfig { Goal = Goal, DecisionReviewMode = ReviewMode.Gate, ReviewerModelId = reviewerRowId };

        await service.RunTurnAsync(runId, teamId, NodeId, Goal, conversationId: null, goalConfig, CancellationToken.None);
    }

    private async Task<IReadOnlyList<SupervisorDecisionRecord>> LedgerAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId)
            .OrderBy(d => d.Sequence)
            .ToListAsync();
    }

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId, Guid userId)
    {
        var workflowId = await CreateSupervisorWorkflowAsync(teamId, userId);
        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    private async Task<Guid> CreateSupervisorWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Messages.Constants.Roles.Admin);
        return await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "sup-rmchecks-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new Messages.Dtos.Workflows.WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<Messages.Dtos.Workflows.NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json("""{"goal":"ship the feature"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<Messages.Dtos.Workflows.EdgeDefinition>
                {
                    new() { From = "start", To = NodeId },
                    new() { From = NodeId, To = "end" },
                },
            },
            Activations = new List<Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task<Guid> SeedCredentialedModelAsync(Guid teamId, string modelId, string baseUrl, string apiKey)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var credId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = credId, TeamId = teamId, Provider = Custom, DisplayName = "live reviewer",
            EncryptedApiKey = scope.Resolve<IPayloadEncryptor>().Encrypt(apiKey), BaseUrl = baseUrl, Status = CredentialStatus.Active,
        });
        var rowId = Guid.NewGuid();
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = rowId, ModelCredentialId = credId, ModelId = modelId, Source = ModelSource.Manual, Enabled = true });
        await db.SaveChangesAsync();
        return rowId;
    }

    /// <summary>No grounded plan review — the ladder falls straight through to the (live) model critic.</summary>
    private sealed class NoAgentPlanReviewer : IAgentPlanReviewer
    {
        public Task<CriticVerdict> ReviewAsync(PlanReviewRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(CriticVerdict.ReviewFailed(ReviewMode.Gate, "agent-reviewer: not requested"));
    }

    /// <summary>A decider that emits a single PLAN with the given subtasks + DependsOn edges — drives the validator over the real turn loop.</summary>
    private sealed class PlanDecider : ISupervisorDecider
    {
        private readonly (string Id, string[]? DependsOn)[] _subtasks;

        public PlanDecider((string Id, string[]? DependsOn)[] subtasks) => _subtasks = subtasks;

        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.Serialize(new
            {
                goal = Goal,
                subtasks = _subtasks.Select(s => s.DependsOn is null
                    ? (object)new { id = s.Id, title = s.Id, instruction = "do " + s.Id }
                    : new { id = s.Id, title = s.Id, instruction = "do " + s.Id, dependsOn = s.DependsOn }),
            }, AgentJson.Options);

            return Task.FromResult(new SupervisorDecision { Kind = SupervisorDecisionKinds.Plan, PayloadJson = payload });
        }
    }
}
