using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Review;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// 🟢 High fidelity — C1's live gate: a QUESTION task at Delivery tier (<see cref="ReviewMode.Gate"/>) really gets a
/// critic verdict. The real <c>claude</c> agent answers a question with no repository, so it produces NO diff — the
/// exact shape whose output review the gate used to skip outright, leaving an ungated answer where an answer is least
/// falsifiable. Drives the real executor against the live gateway and asserts the critic's recorded interaction pair
/// landed on the run's ledger.
///
/// <para>Asserts PRESENCE, never content: whether the reviewer approves this particular answer is a model judgment
/// that would flake; that the review RAN at all is the deterministic wiring C1 fixes. A run that never reaches an
/// inspectable terminal is gateway/exec infra (a non-gating skip), never a false red.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class RealModelAnswerReviewE2ETests
{
    private const string Provider = "Anthropic";

    private readonly PostgresFixture _fixture;

    public RealModelAnswerReviewE2ETests(PostgresFixture fixture) { _fixture = fixture; }

    [SkippableFact]
    public async Task A_question_task_at_delivery_tier_gets_a_critic_verdict()
    {
        var baseUrl = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        var present = new[] { baseUrl, apiKey, model }.Count(v => !string.IsNullOrWhiteSpace(v));
        if (present == 0) throw RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent (fork/local — no live model)");   // skip ≠ pass
        present.ShouldBe(3, "CODESPACE_LLM_* is partially configured — set all three or none; a partial config would self-skip green proving nothing.");

        if (OperatingSystem.IsWindows()) return;                            // the harness + sandbox are /bin/sh based
        if (!await ClaudeReadyAsync()) throw RealModelGate.ReportSkipped(Provider, "the `claude` coding-agent CLI is not installed — this gate needs the harness binary (skip ≠ pass)");

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var credId = await SeedAgentCredentialAsync(teamId, baseUrl!.TrimEnd('/'), apiKey!);

        // An explicit pool ROW so the critic resolves the SAME live model deterministically — an auto-pick that found
        // nothing would fail OPEN, and this gate would then be green over a review that never ran.
        var reviewerRowId = await SeedReviewerPoolRowAsync(teamId, credId, model!);

        await RealModelGate.AssessLiveBestOfNAsync(Provider, async () =>
        {
            var workflowId = await CreateWorkflowAsync(teamId, userId);
            var workflowRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

            // A QUESTION at Delivery tier: no repository, so the agent writes no diff — its whole output is the answer.
            var task = new AgentTask
            {
                Goal = "Answer in two or three sentences: what is the main practical difference between a mutex and a semaphore? Do not create or edit any files.",
                Harness = "claude-code",
                Model = model,
                ModelCredentialId = credId,
                Autonomy = AgentAutonomyLevel.Trusted,
                Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Trusted),
                OutputReviewMode = ReviewMode.Gate,
                ReviewerModelId = reviewerRowId,
                Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "ANSWER.md" }, Kind = BenchmarkGradingKind.ArtifactPresent, Description = "the answer names both primitives and states the difference" },
                TimeoutSeconds = 240,
            };

            Guid runId;
            using (var scope = _fixture.BeginScope())
                runId = (await scope.Resolve<IAgentRunService>().CreateAsync(task, teamId, workflowRunId, "agent-node", "", CancellationToken.None)).Id;

            using (var scope = _fixture.BeginScope())
                await scope.Resolve<IAgentRunExecutor>().ExecuteAsync(runId, CancellationToken.None);

            using var read = _fixture.BeginScope();
            var run = await read.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);

            // A run that never produced a reply tells us nothing about the review wiring — gateway/exec infra, retried
            // by the best-of-N wrapper and skipped (never a red) if every attempt fails there.
            if (!RealModelRunClassifier.HasInspectableModelReply(run))
                throw new AgentExecutionInfraException($"the claude run produced no inspectable reply — gateway/exec infra: status={run.Status}; exitReason={RealModelRunClassifier.ExitReasonOf(run)}; error={run.Error ?? "(none)"}");

            var reviewed = await read.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking()
                .CountAsync(r => r.RunId == workflowRunId
                                 && r.RecordType == WorkflowRunRecordTypes.InteractionCompleted
                                 && EF.Functions.JsonContains(r.PayloadJson, $$"""{"kind":"{{LlmStructuredCritic.OutputReviewCallKind}}"}"""), CancellationToken.None);

            var verdict = reviewed > 0
                ? $"{Provider} '{model}': the text-only answer WAS reviewed — {reviewed} recorded critic verdict(s) on the run's ledger"
                : $"{Provider} '{model}': the text-only answer shipped UNREVIEWED — no critic interaction was recorded (the C1 defect: a Gate-configured answer task skipping its own review)";
            Console.WriteLine($"[answer-review-e2e] {verdict}");

            return (reviewed > 0, verdict);
        });
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);

        return await scope.Resolve<MediatR.IMediator>().Send(new CodeSpace.Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "answer-review-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<CodeSpace.Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    /// <summary>Seed an encrypted gateway credential the executor resolves via <c>ModelCredentialId</c> and the harness projects onto its env. The live key is read from the DB, never in-process.</summary>
    private async Task<Guid> SeedAgentCredentialAsync(Guid teamId, string baseUrl, string apiKey)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var credId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = credId, TeamId = teamId, Provider = Provider, DisplayName = "answer review e2e cred",
            EncryptedApiKey = scope.Resolve<IPayloadEncryptor>().Encrypt(apiKey), BaseUrl = baseUrl, Status = CredentialStatus.Active,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return credId;
    }

    /// <summary>An explicit pool ROW over the SAME credential, so the output critic resolves the live model deterministically instead of relying on an auto-pick that could fail open.</summary>
    private async Task<Guid> SeedReviewerPoolRowAsync(Guid teamId, Guid credId, string modelId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var rowId = Guid.NewGuid();
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = rowId, ModelCredentialId = credId, ModelId = modelId, Source = ModelSource.Manual, Enabled = true });

        await db.SaveChangesAsync();
        return rowId;
    }

    private static async Task<bool> ClaudeReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "claude", Args = new[] { "--version" }, TimeoutSeconds = 15 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }
}
