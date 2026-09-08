using System.Net;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Review;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Llm.Anthropic;
using CodeSpace.Core.Services.Workflows.Llm.OpenAi;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public sealed class CriticObservedModelTests
{
    private const string Alias = "reviewer-alias";
    private const string Key = "synthetic-review-credential";
    private const string Endpoint = "https://review.test/v1";

    public static IEnumerable<object?[]> IndependenceCases()
    {
        yield return ["producer-model", "PRODUCER-MODEL", ReviewModelIndependence.SameBackingModel, false];
        yield return ["producer-model", "independent-model", ReviewModelIndependence.DistinctBackingModel, true];
        yield return ["producer-model", null, ReviewModelIndependence.Unknown, false];
        yield return [null, "independent-model", ReviewModelIndependence.Unknown, false];
    }

    [Theory]
    [MemberData(nameof(IndependenceCases))]
    public async Task A_critic_verdict_calibrates_only_when_both_wire_identities_prove_distinct_backing_models(string? producer, string? reviewer, ReviewModelIndependence expected, bool calibrated)
    {
        var critic = new LlmStructuredCritic(new LLMClientRegistry([new CompatibilityClient(reviewer)]), new PinnedSelector("test"), NullLogger<LlmStructuredCritic>.Instance);
        var request = Request() with { ProducerModel = new ReviewModelIdentity { ObservedModel = producer } };

        var verdict = await critic.ReviewAsync(request, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        verdict.Failed.ShouldBeFalse();
        verdict.Independence.ShouldBe(expected);
        verdict.Calibrated.ShouldBe(calibrated, "configured aliases and missing observations cannot establish evaluator independence");
    }

    public static IEnumerable<object?[]> WireModels()
    {
        foreach (var anthropic in new[] { false, true })
        {
            yield return [anthropic, "producer-model", "producer-model"];
            yield return [anthropic, "independent-model", "independent-model"];
            yield return [anthropic, Alias, Alias];
            yield return [anthropic, null, null];
            yield return [anthropic, "", null];
            yield return [anthropic, "   ", null];
            yield return [anthropic, "model\nforged", null];
            yield return [anthropic, "model\u202Eforged", null];
            yield return [anthropic, new string('m', 501), null];
            yield return [anthropic, new string('m', 500), new string('m', 500)];
            yield return [anthropic, "模型/🧠:version-2", "模型/🧠:version-2"];
            yield return [anthropic, " padded-model", null];
            yield return [anthropic, Key, null];
            yield return [anthropic, "model-" + Key, null];
            yield return [anthropic, Endpoint, null];
        }
    }

    [Theory]
    [MemberData(nameof(WireModels))]
    public async Task The_real_provider_critic_reports_only_a_bounded_nonsecret_wire_identity(bool anthropic, string? wireModel, string? expected)
    {
        using var handler = new ReplyHandler(Response(anthropic, wireModel));
        using var services = HttpServices(handler);
        var provider = anthropic ? (ILLMClient)new AnthropicClient(services.GetRequiredService<IHttpClientFactory>()) : new OpenAiClient(services.GetRequiredService<IHttpClientFactory>());
        var critic = new LlmStructuredCritic(new LLMClientRegistry([provider]), new PinnedSelector(provider.Provider), NullLogger<LlmStructuredCritic>.Instance);

        var verdict = await critic.ReviewAsync(Request(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        verdict.Failed.ShouldBeFalse("model identity uncertainty does not rewrite the existing review outcome");
        verdict.Approved.ShouldBeTrue();
        verdict.ReviewerModel.ShouldBe(expected, "a requested alias is not evidence of the model that answered");
        handler.Calls.ShouldBe(1);
        handler.RequestedModel.ShouldBe(Alias);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_missing_model_property_does_not_turn_the_request_alias_into_an_observation(bool anthropic)
    {
        using var handler = new ReplyHandler(Response(anthropic, null, omitModel: true));
        using var services = HttpServices(handler);
        var factory = services.GetRequiredService<IHttpClientFactory>();
        ILLMClient provider = anthropic ? new AnthropicClient(factory) : new OpenAiClient(factory);
        var critic = new LlmStructuredCritic(new LLMClientRegistry([provider]), new PinnedSelector(provider.Provider), NullLogger<LlmStructuredCritic>.Instance);

        var verdict = await critic.ReviewAsync(Request(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        verdict.Failed.ShouldBeFalse();
        verdict.ReviewerModel.ShouldBeNull();
        handler.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_schema_reask_keeps_only_the_final_responses_observation(bool anthropic, bool finalMissing)
    {
        using var handler = new ReplyHandler(Response(anthropic, "earlier-model", invalid: true), Response(anthropic, finalMissing ? null : "final-model"));
        using var services = HttpServices(handler);
        var factory = services.GetRequiredService<IHttpClientFactory>();
        ILLMClient provider = anthropic ? new AnthropicClient(factory) : new OpenAiClient(factory);
        var critic = new LlmStructuredCritic(new LLMClientRegistry([provider]), new PinnedSelector(provider.Provider), NullLogger<LlmStructuredCritic>.Instance);

        var verdict = await critic.ReviewAsync(Request(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        verdict.Failed.ShouldBeFalse();
        verdict.ReviewerModel.ShouldBe(finalMissing ? null : "final-model");
        handler.Calls.ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prompt_fallback_also_reports_the_final_wire_identity(bool anthropic)
    {
        var answer = JsonSerializer.Serialize(new { approved = true, rationale = "ready" });
        var final = anthropic
            ? JsonSerializer.Serialize(new { model = "fallback-model", content = new[] { new { type = "text", text = answer } } })
            : JsonSerializer.Serialize(new { model = "fallback-model", choices = new[] { new { message = new { content = answer } } } });
        using var handler = new ReplyHandler("{}", final);
        using var services = HttpServices(handler);
        var factory = services.GetRequiredService<IHttpClientFactory>();
        ILLMClient provider = anthropic ? new AnthropicClient(factory) : new OpenAiClient(factory);
        var critic = new LlmStructuredCritic(new LLMClientRegistry([provider]), new PinnedSelector(provider.Provider), NullLogger<LlmStructuredCritic>.Instance);

        var verdict = await critic.ReviewAsync(Request(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        verdict.Failed.ShouldBeFalse();
        verdict.ReviewerModel.ShouldBe("fallback-model");
        handler.Calls.ShouldBe(2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("synthetic-review-credential")]
    public async Task A_legacy_or_unsanitized_client_cannot_turn_its_compatibility_name_into_critic_evidence(string? observed)
    {
        var critic = new LlmStructuredCritic(new LLMClientRegistry([new CompatibilityClient(observed)]), new PinnedSelector("test"), NullLogger<LlmStructuredCritic>.Instance);

        var verdict = await critic.ReviewAsync(Request(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        verdict.Failed.ShouldBeFalse();
        verdict.Approved.ShouldBeTrue();
        verdict.ReviewerModel.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compatibility_model_fallback_remains_available_without_becoming_observed_identity(bool anthropic)
    {
        using var handler = new ReplyHandler(Response(anthropic, null));
        using var services = HttpServices(handler);
        var factory = services.GetRequiredService<IHttpClientFactory>();
        IStructuredLLMClient provider = anthropic ? new AnthropicClient(factory) : new OpenAiClient(factory);
        var completion = await provider.CompleteStructuredAsync(new StructuredLLMCompletionRequest { Model = Alias, SystemPrompt = "review", UserPrompt = "answer", JsonSchema = CriticSchema.GateSchema, Credential = new ResolvedModelCredential { Provider = anthropic ? "Anthropic" : "OpenAI", ApiKey = Key, BaseUrl = Endpoint } }, CancellationToken.None);

        completion.Model.ShouldBe(Alias);
        completion.ObservedModel.ShouldBeNull();
        handler.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_provider_also_rejects_a_secret_known_only_to_the_run_capture_redactor(bool anthropic)
    {
        using var handler = new ReplyHandler(Response(anthropic, "model-run-only-sensitive-value"));
        using var services = HttpServices(handler);
        var factory = services.GetRequiredService<IHttpClientFactory>();
        IStructuredLLMClient provider = anthropic ? new AnthropicClient(factory) : new OpenAiClient(factory);
        using var scope = LlmCallContext.Push(new LlmCallScope(Guid.NewGuid(), Guid.NewGuid(), "critic", "", "critic.review", Logger: null!, Offloader: null!, CaptureRedactor: new PersistenceSecretRedactor(["run-only-sensitive-value"])));

        var completion = await provider.CompleteStructuredAsync(new StructuredLLMCompletionRequest { Model = Alias, SystemPrompt = "review", UserPrompt = "answer", JsonSchema = CriticSchema.GateSchema, Credential = new ResolvedModelCredential { Provider = anthropic ? "Anthropic" : "OpenAI", ApiKey = Key, BaseUrl = Endpoint } }, CancellationToken.None);

        completion.ObservedModel.ShouldBeNull();
        handler.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Pool_failover_preserves_the_last_wire_observation_and_never_uses_the_failed_candidate_trail(bool finalAnthropic, bool finalMissing)
    {
        using var first = new ReplyHandler("{}", "{}", "{}") { Status = HttpStatusCode.ServiceUnavailable };
        using var final = new ReplyHandler(Response(finalAnthropic, finalMissing ? null : "final-observed-model"));
        using var firstServices = HttpServices(first);
        using var finalServices = HttpServices(final);
        var firstFactory = firstServices.GetRequiredService<IHttpClientFactory>();
        var finalFactory = finalServices.GetRequiredService<IHttpClientFactory>();
        IStructuredLLMClient firstProvider = finalAnthropic ? new OpenAiClient(firstFactory) : new AnthropicClient(firstFactory);
        IStructuredLLMClient finalProvider = finalAnthropic ? new AnthropicClient(finalFactory) : new OpenAiClient(finalFactory);
        var initialPick = new ModelPoolPick { ModelId = "failed-candidate-alias", Credential = new ResolvedModelCredential { Provider = firstProvider.Provider, ApiKey = Key, BaseUrl = Endpoint } };
        var finalPick = new ModelPoolPick { ModelId = Alias, Credential = new ResolvedModelCredential { Provider = finalProvider.Provider, ApiKey = Key, BaseUrl = Endpoint } };
        var client = new CapturingFailoverClient(new FailoverStructuredClient([(firstProvider, initialPick), (finalProvider, finalPick)]));
        var critic = new LlmStructuredCritic(new LLMClientRegistry([client]), new PinnedSelector(client.Provider), NullLogger<LlmStructuredCritic>.Instance);

        var verdict = await critic.ReviewAsync(Request(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        verdict.Failed.ShouldBeFalse();
        verdict.Approved.ShouldBeTrue();
        verdict.ReviewerModel.ShouldBe(finalMissing ? null : "final-observed-model");
        var completion = client.Completion.ShouldNotBeNull();
        completion.ObservedModel.ShouldBe(verdict.ReviewerModel);
        completion.Model.ShouldBe(finalMissing ? Alias : "final-observed-model");
        completion.FailedOver.ShouldHaveSingleItem().ShouldContain("failed-candidate-alias");
        completion.FailedOverCause.ShouldNotBeNull().StatusCode.ShouldBe(503);
        first.Calls.ShouldBe(3);
        final.Calls.ShouldBe(1);
        first.RequestedModel.ShouldBe("failed-candidate-alias");
        final.RequestedModel.ShouldBe(Alias);
    }

    private sealed class CapturingFailoverClient(FailoverStructuredClient inner) : ILLMClient, IStructuredLLMClient
    {
        public string Provider => inner.Provider;
        public StructuredLLMCompletion? Completion { get; private set; }
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public async Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken) => Completion = await inner.CompleteStructuredAsync(request, cancellationToken);
    }

    private sealed class CompatibilityClient(string? observed) : ILLMClient, IStructuredLLMClient
    {
        public string Provider => "test";
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken) => Task.FromResult(new StructuredLLMCompletion { Model = "compatibility-name", ObservedModel = observed, Json = JsonSerializer.SerializeToElement(new { approved = true, rationale = "ready" }) });
    }

    private static CriticRequest Request() => new() { Mode = ReviewMode.Gate, ArtifactKind = "test output", Artifact = "completed", Goal = "review the output" };
    private static ServiceProvider HttpServices(HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLlmHttpClients();
        foreach (var name in LlmHttpClientRegistration.ClientNames) services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private static string Response(bool anthropic, string? model, bool omitModel = false, bool invalid = false)
    {
        var verdict = new Dictionary<string, object?> { ["approved"] = true, ["score"] = 9, ["issues"] = Array.Empty<object>() };
        if (!invalid) verdict["rationale"] = "ready";
        var envelope = new Dictionary<string, object?>();
        if (!omitModel) envelope["model"] = model;
        if (anthropic)
        {
            envelope["content"] = new[] { new { type = "tool_use", name = "respond", input = verdict } };
            envelope["usage"] = new { input_tokens = 10, output_tokens = 5 };
        }
        else
        {
            envelope["choices"] = new[] { new { message = new { tool_calls = new[] { new { function = new { name = "respond", arguments = JsonSerializer.Serialize(verdict) } } } } } };
            envelope["usage"] = new { prompt_tokens = 10, completion_tokens = 5 };
        }
        return JsonSerializer.Serialize(envelope);
    }

    private sealed class ReplyHandler(params string[] bodies) : HttpMessageHandler
    {
        private readonly Queue<string> _bodies = new(bodies);
        public int Calls { get; private set; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public string? RequestedModel { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            RequestedModel = json.RootElement.GetProperty("model").GetString();
            var response = new HttpResponseMessage(Status) { Content = new StringContent(_bodies.Dequeue(), Encoding.UTF8, "application/json") };
            if (Status == HttpStatusCode.ServiceUnavailable) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        }
    }

    private sealed class PinnedSelector(string provider) : IModelPoolSelector
    {
        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid rowId, CancellationToken cancellationToken) => Task.FromResult<ModelPoolPick?>(new() { ModelId = Alias, Credential = new ResolvedModelCredential { Provider = provider, ApiKey = Key, BaseUrl = Endpoint } });
        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Guid?> ResolvePinnedBrainRowIdAsync(Guid teamId, Guid rowId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> ResolveTeamDefaultProviderAsync(Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
