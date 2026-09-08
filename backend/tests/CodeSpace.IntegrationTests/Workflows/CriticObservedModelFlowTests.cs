using System.Net;
using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Review;
using CodeSpace.Core.Services.Sessions.Journal.FactsSources;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Llm.Anthropic;
using CodeSpace.Core.Services.Workflows.Llm.OpenAi;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Review;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CriticObservedModelFlowTests(PostgresFixture fixture)
{
    public static IEnumerable<object?[]> Reviews()
    {
        foreach (var anthropic in new[] { false, true })
        foreach (var selection in new[] { "automatic", "pinned", "single" })
        foreach (var observed in new[] { "producer-model", "other-observed-model", null })
            yield return [anthropic, selection, observed];
    }

    [Theory]
    [MemberData(nameof(Reviews))]
    public async Task The_real_pool_and_provider_preserve_operator_selection_but_do_not_invent_reviewer_diversity(bool anthropic, string selection, string? observed)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture, inProcessPool: false);
        using var scope = fixture.BeginScopeAs(userId, teamId);
        var db = scope.Resolve<CodeSpaceDbContext>();
        var credentialId = Guid.NewGuid();
        var producerId = Guid.NewGuid();
        var aliasId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential { Id = credentialId, TeamId = teamId, Provider = anthropic ? "Anthropic" : "OpenAI", DisplayName = "critic identity fixture", EncryptedApiKey = scope.Resolve<IPayloadEncryptor>().Encrypt("synthetic-critic-key"), BaseUrl = "https://critic.test/v1", Status = CredentialStatus.Active });
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = producerId, ModelCredentialId = credentialId, ModelId = "producer-model", Source = ModelSource.Manual, Enabled = true });
        if (selection != "single") db.ModelCredentialModel.Add(new ModelCredentialModel { Id = aliasId, ModelCredentialId = credentialId, ModelId = "reviewer-alias", Source = ModelSource.Manual, Enabled = true });
        await db.SaveChangesAsync();
        var selector = scope.Resolve<IModelPoolSelector>();
        var automatic = await selector.SelectReviewerRowIdAsync(teamId, [anthropic ? "Anthropic" : "OpenAI"], producerId, CancellationToken.None);
        automatic.ShouldBe(selection == "single" ? producerId : aliasId, "the production pool excludes the producer's configured name where possible; a gateway alias can still resolve back to that model");

        using var handler = new ReplyHandler(Response(anthropic, observed));
        var registrations = new ServiceCollection();
        registrations.AddLlmHttpClients();
        foreach (var name in LlmHttpClientRegistration.ClientNames) registrations.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => handler);
        using var httpServices = registrations.BuildServiceProvider();
        var factory = httpServices.GetRequiredService<IHttpClientFactory>();
        ILLMClient provider = anthropic ? new AnthropicClient(factory) : new OpenAiClient(factory);
        var critic = new LlmStructuredCritic(new LLMClientRegistry([new RecordingStreamingStructuredLLMClientDecorator(provider)]), selector, NullLogger<LlmStructuredCritic>.Instance);

        var verdict = await critic.ReviewAsync(new CriticRequest { Mode = ReviewMode.Gate, ArtifactKind = "agent answer", Artifact = "completed output", Goal = "review", ProducerModelRowId = producerId }, teamId, selection == "pinned" ? producerId : null, CancellationToken.None);

        verdict.Failed.ShouldBeFalse("an explicit pin or a one-model pool remains a valid review, and unknown identity does not change approval");
        verdict.Approved.ShouldBeTrue();
        verdict.ReviewerModel.ShouldBe(observed, "missing wire identity stays unknown, and aliases cannot create evidence of a different reviewer");
        handler.Calls.ShouldBe(1);
        handler.RequestedModel.ShouldBe(selection == "automatic" ? "reviewer-alias" : "producer-model");

        var runId = Guid.NewGuid();
        var decisions = new List<(Guid Id, bool ProducerKnown)>();
        foreach (var producerKnown in new[] { true, false })
        {
            var outcome = SupervisorOutcome.WriteModelUsage("{}", producerKnown ? new SupervisorModelUsage { Model = "producer-model", ObservedModel = "producer-model" } : null);
            outcome = SupervisorOutcome.WriteReviews(outcome, [new SupervisorDecisionReview { Approved = verdict.Approved, Rationale = verdict.Rationale, ReviewerModelId = verdict.ReviewerModel, Independence = verdict.Independence, Scope = "decision" }]);
            var id = Guid.NewGuid();
            db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord { Id = id, TeamId = teamId, SupervisorRunId = runId, DecisionKind = SupervisorDecisionKinds.Plan, IdempotencyKey = $"critic-identity:{id:N}", InputHash = new string('0', 64), Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = outcome });
            decisions.Add((id, producerKnown));
        }
        await db.SaveChangesAsync();
        using var read = fixture.BeginScopeAs(userId, teamId);
        var facts = await read.Resolve<DecisionReviewFactsSource>().GatherAsync(runId, teamId, CancellationToken.None);
        foreach (var decision in decisions)
        {
            var review = facts[DecisionReviewTimelineMap.EventId(decision.Id, 0)].Review.ShouldNotBeNull();
            review.ReviewerModel.ShouldBe(observed);
            ((bool?)review.SameModelAsProducer).ShouldBe(observed is null || !decision.ProducerKnown ? null : observed == "producer-model", "persisting a review and reading its journal facts cannot promote missing model identity into a different-model claim");
        }
    }

    /// <summary>
    /// The SHARED integration double (registered once, reused by dozens of pre-existing flows through the critic)
    /// now echoes a known wire identity by default instead of leaving every one of those flows in the
    /// unknown-identity branch by omission. Proven through the REAL <see cref="LlmStructuredCritic"/> resolved from
    /// DI (not an ad-hoc HTTP mock, unlike the theory above) and the REAL <see cref="DecisionReviewFactsSource"/> —
    /// the known identity a real run's Room card reads.
    /// </summary>
    [Fact]
    public async Task The_shared_critic_fake_reports_a_known_identity_that_survives_the_journal_round_trip()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture, inProcessPool: false);
        using var scope = fixture.BeginScopeAs(userId, teamId);
        var (_, criticRowId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(fixture, teamId, "critic-model", provider: DeterministicCriticLlmClient.ProviderTag);

        var critic = scope.Resolve<IStructuredCritic>();
        var verdict = await critic.ReviewAsync(new CriticRequest { Mode = ReviewMode.Gate, ArtifactKind = "agent answer", Artifact = "completed output", Goal = "review" }, teamId, criticRowId, CancellationToken.None);

        verdict.Failed.ShouldBeFalse();
        verdict.ReviewerModel.ShouldBe("critic-model", "the shared fake now echoes a known wire identity, not just the compatibility Model field");

        var runId = Guid.NewGuid();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var outcome = SupervisorOutcome.WriteReviews(SupervisorOutcome.WriteModelUsage("{}", new SupervisorModelUsage { Model = "producer-model", ObservedModel = "producer-model" }),
            [new SupervisorDecisionReview { Approved = verdict.Approved, Rationale = verdict.Rationale, ReviewerModelId = verdict.ReviewerModel, Independence = ReviewModelIndependence.DistinctBackingModel, Scope = "decision" }]);
        var decisionId = Guid.NewGuid();
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord { Id = decisionId, TeamId = teamId, SupervisorRunId = runId, DecisionKind = SupervisorDecisionKinds.Plan, IdempotencyKey = $"shared-critic-fake:{decisionId:N}", InputHash = new string('0', 64), Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = outcome });
        await db.SaveChangesAsync();

        using var read = fixture.BeginScopeAs(userId, teamId);
        var review = (await read.Resolve<DecisionReviewFactsSource>().GatherAsync(runId, teamId, CancellationToken.None))[DecisionReviewTimelineMap.EventId(decisionId, 0)].Review.ShouldNotBeNull();
        review.ReviewerModel.ShouldBe("critic-model", "the known identity the shared fake now reports reaches the journal facts a real run's Room card reads");
        review.SameModelAsProducer.ShouldBe(false, "a known, differently-named reviewer is known-and-different, not unknown");
        review.Calibrated.ShouldBeTrue();
    }

    /// <summary>The shared fake's ONE deliberate unknown-identity flow (<see cref="DeterministicCriticLlmClient.UnknownIdentityMarker"/>) — proves the default-known-identity change above did not erase the unknown branch from the fake's own repertoire.</summary>
    [Fact]
    public async Task The_shared_critic_fakes_unknown_identity_marker_still_reports_no_observed_model()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture, inProcessPool: false);
        using var scope = fixture.BeginScopeAs(userId, teamId);
        var (_, criticRowId) = await WorkflowsTestSeed.SeedCredentialedModelAsync(fixture, teamId, "critic-model", provider: DeterministicCriticLlmClient.ProviderTag);

        var critic = scope.Resolve<IStructuredCritic>();
        var verdict = await critic.ReviewAsync(new CriticRequest { Mode = ReviewMode.Gate, ArtifactKind = "agent answer", Artifact = DeterministicCriticLlmClient.UnknownIdentityMarker, Goal = "review" }, teamId, criticRowId, CancellationToken.None);

        verdict.Failed.ShouldBeFalse("a missing wire identity does not fail the review — only the reviewer name goes unknown");
        verdict.ReviewerModel.ShouldBeNull();
    }

    private static string Response(bool anthropic, string? observed)
    {
        var verdict = new { approved = true, score = 9, issues = Array.Empty<object>(), rationale = "ready" };
        return anthropic
            ? JsonSerializer.Serialize(new { model = observed, content = new[] { new { type = "tool_use", name = "respond", input = verdict } }, usage = new { input_tokens = 10, output_tokens = 5 } })
            : JsonSerializer.Serialize(new { model = observed, choices = new[] { new { message = new { tool_calls = new[] { new { function = new { name = "respond", arguments = JsonSerializer.Serialize(verdict) } } } } } }, usage = new { prompt_tokens = 10, completion_tokens = 5 } });
    }

    private sealed class ReplyHandler(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? RequestedModel { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            RequestedModel = json.RootElement.GetProperty("model").GetString();
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
