using System.Net;
using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Review;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Llm.Anthropic;
using CodeSpace.Core.Services.Workflows.Llm.OpenAi;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RubricJudgeObservedModelFlowTests(PostgresFixture fixture)
{
    public static IEnumerable<object?[]> Judgments()
    {
        foreach (var anthropic in new[] { false, true })
        foreach (var pinned in new[] { false, true })
        foreach (var observed in new[] { "producer-model", "judge-wire-model", null })
            yield return [anthropic, pinned, observed];
    }

    [Theory]
    [MemberData(nameof(Judgments))]
    public async Task The_real_pool_and_provider_make_rubric_independence_a_wire_observation(bool anthropic, bool pinned, string? observed)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture, inProcessPool: false);
        using var scope = fixture.BeginScopeAs(userId, teamId);
        var db = scope.Resolve<CodeSpaceDbContext>();
        var credentialId = Guid.NewGuid();
        var producerId = Guid.NewGuid();
        var judgeId = Guid.NewGuid();
        var providerName = anthropic ? "Anthropic" : "OpenAI";
        db.ModelCredential.Add(new ModelCredential { Id = credentialId, TeamId = teamId, Provider = providerName, DisplayName = "rubric identity fixture", EncryptedApiKey = scope.Resolve<IPayloadEncryptor>().Encrypt("synthetic-rubric-key"), BaseUrl = "https://rubric.test/v1", Status = CredentialStatus.Active });
        db.ModelCredentialModel.AddRange(
            new ModelCredentialModel { Id = producerId, ModelCredentialId = credentialId, ModelId = "producer-model", Source = ModelSource.Manual, Enabled = true },
            new ModelCredentialModel { Id = judgeId, ModelCredentialId = credentialId, ModelId = "judge-alias", Source = ModelSource.Manual, Enabled = true });
        await db.SaveChangesAsync();

        using var handler = new ReplyHandler(Response(anthropic, observed));
        var registrations = new ServiceCollection();
        registrations.AddLlmHttpClients();
        foreach (var name in LlmHttpClientRegistration.ClientNames) registrations.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => handler);
        using var httpServices = registrations.BuildServiceProvider();
        var factory = httpServices.GetRequiredService<IHttpClientFactory>();
        ILLMClient provider = anthropic ? new AnthropicClient(factory) : new OpenAiClient(factory);
        var judge = new LlmRubricJudge(new LLMClientRegistry([provider]), scope.Resolve<IModelPoolSelector>());
        var rubric = new AcceptanceRubric
        {
            JudgeModelId = pinned ? producerId : null,
            Criteria = [new AcceptanceRubricCriterion { Id = "complete", Requirement = "The answer is complete." }],
        };

        var verdict = await judge.JudgeAsync(new RubricJudgeRequest
        {
            Rubric = rubric,
            Artifact = "complete answer",
            TeamId = teamId,
            ProducerModel = new ReviewModelIdentity { ModelCredentialModelId = producerId, ConfiguredModel = "producer-model", ObservedModel = "producer-model" },
        }, CancellationToken.None);

        verdict.Failed.ShouldBeFalse();
        verdict.JudgeModel.ShouldBe(observed, "a missing provider observation remains unknown and the configured alias is never substituted");
        verdict.Independence.ShouldBe(observed is null ? ReviewModelIndependence.Unknown : observed == "producer-model" ? ReviewModelIndependence.SameBackingModel : ReviewModelIndependence.DistinctBackingModel);
        verdict.Calibrated.ShouldBe(observed == "judge-wire-model");
        handler.RequestedModel.ShouldBe(pinned ? "producer-model" : "judge-alias", "an explicit pin is preserved; automatic selection avoids the producer row when the pool permits it");
    }

    private static string Response(bool anthropic, string? observed)
    {
        var verdict = new { criteria = new[] { new { id = "complete", met = true, evidence = "complete answer" } } };
        return anthropic
            ? JsonSerializer.Serialize(new { model = observed, content = new[] { new { type = "tool_use", name = "respond", input = verdict } }, usage = new { input_tokens = 10, output_tokens = 5 } })
            : JsonSerializer.Serialize(new { model = observed, choices = new[] { new { message = new { tool_calls = new[] { new { function = new { name = "respond", arguments = JsonSerializer.Serialize(verdict) } } } } } }, usage = new { prompt_tokens = 10, completion_tokens = 5 } });
    }

    private sealed class ReplyHandler(string body) : HttpMessageHandler
    {
        public string? RequestedModel { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            RequestedModel = json.RootElement.GetProperty("model").GetString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
