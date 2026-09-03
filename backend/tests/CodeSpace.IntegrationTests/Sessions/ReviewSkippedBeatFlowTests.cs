using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Review;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;
using CodeSpace.Messages.Tasks.Timeline;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Sessions;

/// <summary>
/// 🟢 What a review that did NOT run leaves behind, end to end against real Postgres: the REAL
/// <see cref="LlmStructuredCritic"/>, faulted at its structured client, writing through the REAL
/// <see cref="IRunRecordLogger"/> onto a real run's ledger — read back through the SAME narrative timeline source the
/// journal reads, so the assertion is "a user can see it", not "a row exists".
///
/// <para>The critic FAILS OPEN by contract: the caller keeps the producer's original output. That is correct and stays
/// correct. What was wrong is that it happened silently — a revoked reviewer credential turned a configured review off
/// with no trace on the ledger, in the journal, or in a log. These tests pin the trace.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ReviewSkippedBeatFlowTests
{
    private readonly PostgresFixture _fixture;

    public ReviewSkippedBeatFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_faulted_review_leaves_a_review_skipped_beat_the_journal_reads_back()
    {
        var run = await SeedRunAsync();

        await ReviewAsync(run, new ThrowingStructuredClient(new InvalidOperationException("the reviewer credential was revoked")));

        var beat = (await NarrativeAsync(run)).ShouldHaveSingleItem();

        beat.Title.ShouldBe("Review skipped", "the backend owns the copy the journal renders");
        beat.Severity.ShouldBe(TimelineSeverity.Warning);
        beat.Level.ShouldBe(TimelineLevel.Milestone, "an unreviewed output is a story beat — folding it away is the silence this fixes");
        beat.Summary.ShouldBe("InvalidOperationException: the reviewer credential was revoked", "the reason names WHY, so the operator can act on it");
    }

    [Fact]
    public async Task A_review_that_ran_leaves_no_skipped_beat()
    {
        // The other half: a beat on the happy path would make every run look unreviewed and the signal worthless.
        var run = await SeedRunAsync();

        var verdict = await ReviewAsync(run, new ApprovingStructuredClient());

        verdict.Failed.ShouldBeFalse();
        verdict.ReviewerModel.ShouldBe("claude-opus-4-8", "a verdict that HAPPENED names the model it ran on");
        (await NarrativeAsync(run)).ShouldBeEmpty();
    }

    /// <summary>Run the REAL critic against <paramref name="client"/> under a real ledger-writing scope bound to the seeded run.</summary>
    private async Task<CriticVerdict> ReviewAsync(SeededRun run, ILLMClient client)
    {
        using var scope = _fixture.BeginScope();

        var critic = new LlmStructuredCritic(new SingleClientRegistry(client), new FixedPickSelector(), NullLogger<LlmStructuredCritic>.Instance);

        using (LlmCallContext.Push(new LlmCallScope(run.RunId, run.TeamId, "sup", "sup#turn0", "supervisor.decision", scope.Resolve<IRunRecordLogger>(), Offloader: null!)))
        {
            return await critic.ReviewAsync(
                new CriticRequest { Mode = ReviewMode.Gate, ArtifactKind = CriticArtifactKinds.SupervisorDecision, Artifact = "spawn: {}", Goal = "ship" },
                run.TeamId, reviewerModelId: Guid.NewGuid(), CancellationToken.None);
        }
    }

    /// <summary>The run's review-skipped beats, read through the SAME narrative source + pushed-down record filter the journal uses — a beat the filter drops is a beat nobody sees.</summary>
    private async Task<IReadOnlyList<RunTimelineEvent>> NarrativeAsync(SeededRun run)
    {
        using var scope = _fixture.BeginScope();

        var events = await scope.Resolve<RunRecordTimelineSource>().ContributeAsync(new RunTimelineContext { RunId = run.RunId, TeamId = run.TeamId }, CancellationToken.None);

        return events.Where(e => e.Kind == WorkflowRunRecordTypes.ReviewSkipped).ToList();
    }

    private async Task<SeededRun> SeedRunAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var workflowId = await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "review-skipped-" + Guid.NewGuid().ToString("N")[..6],
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });

        return new SeededRun(teamId, await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId));
    }

    private sealed record SeededRun(Guid TeamId, Guid RunId);

    private sealed class ThrowingStructuredClient : ILLMClient, IStructuredLLMClient
    {
        private readonly Exception _fault;

        public ThrowingStructuredClient(Exception fault) { _fault = fault; }

        public string Provider => "Anthropic";

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken) => throw _fault;
    }

    private sealed class ApprovingStructuredClient : ILLMClient, IStructuredLLMClient
    {
        public string Provider => "Anthropic";

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new StructuredLLMCompletion
            {
                Json = System.Text.Json.JsonDocument.Parse("""{ "approved": true, "issues": [], "rationale": "sound" }""").RootElement.Clone(),
                Model = "claude-opus-4-8",
            });
    }

    private sealed class SingleClientRegistry : ILLMClientRegistry
    {
        public SingleClientRegistry(ILLMClient client) => All = new[] { client };
        public IReadOnlyList<ILLMClient> All { get; }
        public ILLMClient Resolve(string provider) => All[0];
    }

    /// <summary>Resolves any reviewer row to one Anthropic pick — the pool itself is covered by <c>ModelPoolSelectorFlowTests</c>; this suite is about what the ledger keeps.</summary>
    private sealed class FixedPickSelector : IModelPoolSelector
    {
        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) =>
            Task.FromResult<ModelPoolPick?>(new ModelPoolPick { ModelId = "claude-opus-4-8", Credential = new Messages.Agents.ResolvedModelCredential { Provider = "Anthropic", ApiKey = "sk" } });

        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Guid?> ResolvePinnedBrainRowIdAsync(Guid teamId, Guid modelCredentialModelId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> ResolveTeamDefaultProviderAsync(Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
