using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Review;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// End-to-end-through-Postgres proof that the agent run executor's ONE in-process model call — the output-review critic
/// — records its <c>interaction.*</c> onto the SAME <c>workflow_run_record</c> ledger as the rest of the run. The
/// executor runs in a Hangfire job OUTSIDE the engine's per-node <c>LlmCallScope</c>, so before the fix its critic call
/// captured nothing. Drives the REAL <see cref="AgentRunExecutor"/> with the REAL scoped ledger writer + the REAL
/// recording-decorated structured client (a registered deterministic fake the critic resolves by provider) + real
/// DbContext — the exact production write path a fake-logger unit test can't cover (a DI-scope-lifetime or EF break
/// would ship green behind fail-open). Mirrors <see cref="RunRecordInteractionFlowTests"/>'s real-persistence pattern.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentRunExecutorRecordingFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentRunExecutorRecordingFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task The_output_review_critic_records_a_correlated_interaction_pair_onto_the_workflow_run_ledger()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // The reviewer model sits under a REGISTERED structured fake provider, so the critic resolves a client and
        // makes a real (recording-decorator-wrapped) structured call — the call whose interaction we assert lands.
        var (_, reviewerModelId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "reviewer-model", provider: DeterministicWorkPlanLlmClient.ProviderTag);

        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var task = new AgentTask { Goal = "ship the widget", Harness = "codex-cli", OutputReviewMode = ReviewMode.Gate, ReviewerModelId = reviewerModelId };
        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = "did it", ChangedFiles = new[] { "src/widget.cs" }, Patch = "diff --git a/src/widget.cs b/src/widget.cs\n+// change" };

        using (var scope = _fixture.BeginScope())
        {
            // A persisted agent run spawned by this workflow run's agent.run node — the (WorkflowRunId, NodeId) cell
            // the interaction records key onto.
            var agentRun = await scope.Resolve<IAgentRunService>().CreateAsync(task, teamId, runId, "agent-node", "", CancellationToken.None);

            await BuildExecutor(scope).ReviewOutputIfEnabledAsync(task, result, agentRun, CancellationToken.None);
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var interactions = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.NodeId == "agent-node" && (r.RecordType == WorkflowRunRecordTypes.InteractionStarted || r.RecordType == WorkflowRunRecordTypes.InteractionCompleted))
            .OrderBy(r => r.Sequence)
            .ToListAsync();

        interactions.Select(r => r.RecordType).ShouldBe(new[] { WorkflowRunRecordTypes.InteractionStarted, WorkflowRunRecordTypes.InteractionCompleted },
            customMessage: "the executor pushes a scope around the critic so its model call — from a Hangfire job outside the engine's node scope — records a correlated pair on the run's ledger");
        interactions[0].CorrelationId.ShouldNotBeNull();
        interactions[0].CorrelationId.ShouldBe(interactions[1].CorrelationId, "the started+completed pair share one correlation id");

        var started = JsonDocument.Parse(interactions[0].PayloadJson).RootElement;
        started.GetProperty("kind").GetString().ShouldBe(LlmStructuredCritic.ReviewCallKind,
            customMessage: "every critic call records under the critic's OWN kind (K/L2 — the journal's intent label); the executor's agent.critic push still provides the identity cell, its kind shadowed by the critic's re-label");
    }

    /// <summary>
    /// C1 — the same real write path for a run whose whole output is TEXT. Until this fix the gate demanded a diff,
    /// so an answer/report run under a configured Gate reviewed NOTHING and left no interaction on the ledger at all:
    /// the operator's Delivery-tier review silently did not happen. Asserts the recorded pair AND that what the
    /// reviewer was shown is the ANSWER (its captured deliverable's real bytes, materialized back out of the store),
    /// not an empty diff.
    /// </summary>
    [Fact]
    public async Task A_text_only_answer_records_its_review_and_shows_the_reviewer_the_captured_deliverable()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (_, reviewerModelId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "reviewer-model", provider: DeterministicWorkPlanLlmClient.ProviderTag);

        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var task = new AgentTask { Goal = "compare the two runtimes", Harness = "codex-cli", OutputReviewMode = ReviewMode.Gate, ReviewerModelId = reviewerModelId };

        // No ChangedFiles and no Patch — the shape the pre-C1 gate skipped outright.
        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = "wrote the comparison", CapturedArtifactCount = 1 };

        using (var scope = _fixture.BeginScope())
        {
            var agentRun = await scope.Resolve<IAgentRunService>().CreateAsync(task, teamId, runId, "agent-node", "", CancellationToken.None);
            await SeedCapturedDeliverableAsync(teamId, runId, agentRun.Id, "DELIVERABLE.md", "# Comparison\nRust wins on safety; Go wins on build speed.");

            await BuildExecutor(scope).ReviewOutputIfEnabledAsync(task, result, agentRun, CancellationToken.None);
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var interactions = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.NodeId == "agent-node" && (r.RecordType == WorkflowRunRecordTypes.InteractionStarted || r.RecordType == WorkflowRunRecordTypes.InteractionCompleted))
            .OrderBy(r => r.Sequence)
            .ToListAsync();

        interactions.Select(r => r.RecordType).ShouldBe(new[] { WorkflowRunRecordTypes.InteractionStarted, WorkflowRunRecordTypes.InteractionCompleted },
            customMessage: "the answer WAS reviewed — the run's journal now carries the verdict a text-only Delivery-tier run used to ship without");

        var prompt = await ResolvePromptAsync(verify, teamId, JsonDocument.Parse(interactions[0].PayloadJson).RootElement);

        prompt.ShouldContain("Rust wins on safety", customMessage: "the reviewer reads the report the agent actually wrote, materialized from the artifact store");
        prompt.ShouldContain("compare the two runtimes", customMessage: "and judges it against the run's goal");
    }

    /// <summary>The started record's user prompt, resolved through the offloader — a large prompt rides an <c>$artifact_id</c> reference instead of the inline string, so both shapes have to be read.</summary>
    private static async Task<string> ResolvePromptAsync(ILifetimeScope scope, Guid teamId, JsonElement started)
    {
        var user = started.GetProperty("prompt").GetProperty("user");

        var inline = user.ValueKind == JsonValueKind.String ? user.GetString() : null;
        var artifactId = user.ValueKind == JsonValueKind.Object && user.TryGetProperty("$artifact_id", out var id) ? Guid.Parse(id.GetString()!) : (Guid?)null;

        return await scope.Resolve<IArtifactOffloader>().ResolveAsync(teamId, inline, artifactId, CancellationToken.None);
    }

    /// <summary>Capture one real deliverable for the run: CAS bytes plus the typed manifest row the answer render reads back.</summary>
    private async Task SeedCapturedDeliverableAsync(Guid teamId, Guid runId, Guid agentRunId, string path, string content)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var payload = System.Text.Encoding.UTF8.GetBytes(content);
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload));
        var artifactId = Guid.NewGuid();

        db.WorkflowArtifact.Add(new CodeSpace.Core.Persistence.Entities.WorkflowArtifact
        {
            Id = artifactId, TeamId = teamId, Sha256 = sha, ContentType = "text/markdown", SizeBytes = payload.Length, InlineBytes = payload, CreatedAt = now,
        });
        db.ArtifactManifest.Add(new CodeSpace.Core.Persistence.Entities.ArtifactManifest
        {
            Id = Guid.NewGuid(), TeamId = teamId, AgentRunId = agentRunId, WorkflowRunId = runId, FenceEpoch = 1,
            Kind = ArtifactManifestKind.Document, LogicalPath = path, ContentArtifactId = artifactId,
            Sha256 = sha, SizeBytes = payload.Length, ContentType = "text/markdown",
            CreatedDate = now, LastModifiedDate = now,
        });
        await db.SaveChangesAsync();
    }

    // Construct the REAL executor with the review path's live collaborators resolved from the scope; the harness /
    // sandbox / workspace deps (params 2-8) are untouched by ReviewOutputIfEnabledAsync, so they ride as null.
    private static AgentRunExecutor BuildExecutor(ILifetimeScope scope) => new(
        scope.Resolve<IAgentRunService>(),
        null!, null!, null!, null!, null!, null!, null!,
        scope.Resolve<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
        scope.Resolve<CodeSpaceDbContext>(),
        scope.Resolve<IStructuredCritic>(),
        scope.Resolve<IArtifactOffloader>(),
        scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactStore>(),
        scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IPublishManifestStore>(), scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IArtifactManifestStore>(), scope.Resolve<CodeSpace.Core.Services.Agents.Capture.ICaptureIntentService>(),
        scope.Resolve<IEnumerable<CodeSpace.Core.Services.Agents.Publish.IPublishGuard>>(),
        NullLogger<AgentRunExecutor>.Instance);

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<MediatR.IMediator>();
        return await mediator.Send(new CodeSpace.Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "agent-rec-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<CodeSpace.Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }
}
