using System.Net;
using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Llm.Anthropic;
using CodeSpace.Core.Services.Workflows.Llm.OpenAi;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit.Abstractions;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>PostgreSQL and production HTTP-pipeline acceptance for physical receipts; scripted responses are not live-model qualification.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed partial class PhysicalStructuredPostAccountingFlowTests(PostgresFixture fixture, ITestOutputHelper output)
{
    [Theory]
    [InlineData("Anthropic", "forced-fallback", 2)]
    [InlineData("OpenAI", "forced-fallback", 2)]
    [InlineData("Anthropic", "tool-rejected", 2)]
    [InlineData("OpenAI", "tool-rejected", 2)]
    [InlineData("Anthropic", "parse-reask", 3)]
    [InlineData("OpenAI", "parse-reask", 3)]
    [InlineData("Anthropic", "schema-reask", 2)]
    [InlineData("OpenAI", "schema-reask", 2)]
    [InlineData("Anthropic", "consumer-reask", 2)]
    [InlineData("OpenAI", "consumer-reask", 2)]
    [InlineData("Anthropic", "transport-retry", 2)]
    [InlineData("OpenAI", "transport-retry", 2)]
    [InlineData("Anthropic", "pool-failover", 4)]
    [InlineData("OpenAI", "pool-failover", 4)]
    public async Task Every_physical_POST_has_independent_durable_admission_before_it_is_sent(string provider, string path, int expectedPosts)
    {
        var scenario = await SeedAsync();
        using var handler = new ProtocolHandler(provider, Replies(provider, path), () => ReservationIdsAsync(scenario));
        using var services = HttpServices(handler);
        using var scope = fixture.BeginScope();
        var factory = services.GetRequiredService<IHttpClientFactory>();
        var request = Request(provider, path);
        IStructuredLLMClient client = Decorated(provider, factory);
        if (path == "pool-failover")
        {
            var alternate = provider == "Anthropic" ? "OpenAI" : "Anthropic";
            client = new FailoverStructuredClient(new[]
            {
                (client, new ModelPoolPick { ModelId = "fixture-model", Credential = request.Credential! }),
                (Decorated(alternate, factory), new ModelPoolPick { ModelId = "fixture-model", Credential = Credential(alternate) }),
            });
        }
        StructuredLLMCompletion result;
        using (Push(scope, scenario)) result = await client.CompleteStructuredAsync(request, CancellationToken.None);
        result.Json.GetProperty("approved").GetBoolean().ShouldBeTrue("the logical call really succeeded before its physical accounting is assessed");
        handler.Admissions.Count.ShouldBe(expectedPosts, "count the actual primary-handler POST entries through the production retry pipeline");
        var census = await CensusAsync(scenario, handler, result);
        output.WriteLine(JsonSerializer.Serialize(new { provider, path, census }));

        for (var i = 0; i < expectedPosts; i++) handler.Admissions[i].Distinct().Count().ShouldBeGreaterThanOrEqualTo(i + 1, "an earlier logical claim cannot admit an additional physical POST");
        using var read = fixture.BeginScope();
        var attempts = await read.Resolve<CodeSpaceDbContext>().WorkflowRunModelCallAttempt.AsNoTracking().Where(a => a.WorkflowRunId == scenario.RunId).OrderBy(a => a.AttemptOrdinal).ToArrayAsync();
        attempts.Length.ShouldBe(expectedPosts);
        attempts.Select(a => a.ModelCallId).Distinct().Count().ShouldBe(1, "pool candidates remain one logical operation");
        attempts.Select(a => a.AttemptOrdinal).ShouldBe(Enumerable.Range(1, expectedPosts));
        attempts.All(a => a.CaptureSource == "structured-post/v1").ShouldBeTrue();
        if (path == "parse-reask")
        {
            result.Usage.InputTokens.ShouldBe(210);
            result.Usage.OutputTokens.ShouldBe(205);
            result.Usage.IsPartial.ShouldBeFalse("all three complete wire envelopes survived structured parse failures");
            attempts.Sum(a => a.CostAmount).ShouldBe(0.000415m);
        }
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task A_schema_reask_cannot_send_after_the_previous_POST_consumed_the_cap(string provider)
    {
        var scenario = await SeedAsync();
        using var handler = new ProtocolHandler(provider, new[] { Structured(provider, "{}", 1_000_000), Success(provider) }, () => ReservationIdsAsync(scenario));
        using var services = HttpServices(handler);
        using var scope = fixture.BeginScope();
        using (Push(scope, scenario, 0.01m))
            await Should.ThrowAsync<LlmBudgetExceededException>(() => Decorated(provider, services.GetRequiredService<IHttpClientFactory>()).CompleteStructuredAsync(Request(provider, "schema-reask"), CancellationToken.None));
        handler.Admissions.Count.ShouldBe(1, "the refused second POST never reaches the primary handler");
        using var read = fixture.BeginScope();
        var claims = await read.Resolve<CodeSpaceDbContext>().BudgetReservation.AsNoTracking().Where(a => a.WorkflowRunId == scenario.RunId).ToArrayAsync();
        claims.Length.ShouldBe(1);
        claims[0].SettledUsd.ShouldBe(1.000005m, "actual observed spend may exceed an estimate; never clamp a bill to the cap");
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task A_lost_response_after_a_sent_POST_cannot_be_settled_as_a_complete_single_request_bill(string provider)
    {
        var scenario = await SeedAsync();
        using var handler = new ProtocolHandler(provider, new[] { new Reply(HttpStatusCode.OK, "", LostResponse: true), Success(provider) }, () => ReservationIdsAsync(scenario));
        using var services = HttpServices(handler);
        using var scope = fixture.BeginScope();
        StructuredLLMCompletion result;
        using (Push(scope, scenario)) result = await Decorated(provider, services.GetRequiredService<IHttpClientFactory>()).CompleteStructuredAsync(Request(provider, "transport-retry"), CancellationToken.None);
        result.Json.GetProperty("approved").GetBoolean().ShouldBeTrue();
        handler.Admissions.Count.ShouldBe(2);
        output.WriteLine(JsonSerializer.Serialize(new { provider, path = "ack-loss", census = await CensusAsync(scenario, handler, result) }));

        // The scripted server received the first POST before losing its response. Its charge is unknown, not zero.
        result.Usage.IsPartial.ShouldBeTrue("a successful Polly retry does not prove the preceding sent request was free");
        using var read = fixture.BeginScope();
        var rows = await read.Resolve<CodeSpaceDbContext>().BudgetReservation.AsNoTracking().Where(r => r.WorkflowRunId == scenario.RunId).ToListAsync();
        rows.ShouldContain(r => r.State == BudgetReservationStates.Indeterminate && r.SettledUsd == null);
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task Missing_output_usage_on_one_POST_is_unknown_not_provider_reported_zero(string provider)
    {
        var scenario = await SeedAsync();
        var full = Success(provider);
        var partial = full with { Body = full.Body.Replace(provider == "Anthropic" ? ",\"output_tokens\":5" : ",\"completion_tokens\":5", "") };
        using var handler = new ProtocolHandler(provider, new[] { partial }, () => ReservationIdsAsync(scenario));
        using var services = HttpServices(handler);
        using var scope = fixture.BeginScope();
        StructuredLLMCompletion result;
        using (Push(scope, scenario)) result = await Decorated(provider, services.GetRequiredService<IHttpClientFactory>()).CompleteStructuredAsync(Request(provider, "single"), CancellationToken.None);
        result.Json.GetProperty("approved").GetBoolean().ShouldBeTrue();
        handler.Admissions.Count.ShouldBe(1);
        output.WriteLine(JsonSerializer.Serialize(new { provider, path = "missing-output-usage", census = await CensusAsync(scenario, handler, result) }));
        result.Usage.OutputTokens.ShouldBeNull("absence is not a provider-reported zero");
        result.Usage.HasCompleteTokenCounts.ShouldBeFalse();
        using var read = fixture.BeginScope();
        var reservation = await read.Resolve<CodeSpaceDbContext>().BudgetReservation.AsNoTracking().SingleAsync(r => r.WorkflowRunId == scenario.RunId);
        reservation.State.ShouldBe(BudgetReservationStates.Indeterminate);
        reservation.SettledUsd.ShouldBeNull();
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task Missing_observed_model_cannot_be_priced_from_the_completion_fallback_model(string provider)
    {
        var scenario = await SeedAsync();
        var response = Success(provider);
        response = response with { Body = response.Body.Replace("\"model\":\"fixture-model\",", "") };
        using var handler = new ProtocolHandler(provider, new[] { response }, () => ReservationIdsAsync(scenario));
        using var services = HttpServices(handler);
        using var scope = fixture.BeginScope();
        StructuredLLMCompletion result;
        using (Push(scope, scenario)) result = await Decorated(provider, services.GetRequiredService<IHttpClientFactory>()).CompleteStructuredAsync(Request(provider, "single"), CancellationToken.None);
        result.Json.GetProperty("approved").GetBoolean().ShouldBeTrue();
        result.Usage.IsPartial.ShouldBeTrue("the provider completion's requested-model fallback is not an observed billing model");
        using var read = fixture.BeginScope();
        (await read.Resolve<CodeSpaceDbContext>().BudgetReservation.SingleAsync(r => r.WorkflowRunId == scenario.RunId)).SettledUsd.ShouldBeNull();
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task A_format_character_forged_wire_model_cannot_be_persisted_as_the_effective_model(string provider)
    {
        var scenario = await SeedAsync();
        var response = Success(provider);
        // A right-to-left override embedded in the wire "model" field — the same forged value CriticObservedModelTests
        // pins for the critic path. No credential/capture secret matches it, so only the shared control/format-char
        // rejection (not redaction) can catch it here.
        response = response with { Body = response.Body.Replace("\"fixture-model\"", "\"fixture\u202Eforged\"") };
        using var handler = new ProtocolHandler(provider, new[] { response }, () => ReservationIdsAsync(scenario));
        using var services = HttpServices(handler);
        using var scope = fixture.BeginScope();
        StructuredLLMCompletion result;
        using (Push(scope, scenario)) result = await Decorated(provider, services.GetRequiredService<IHttpClientFactory>()).CompleteStructuredAsync(Request(provider, "single"), CancellationToken.None);
        result.Json.GetProperty("approved").GetBoolean().ShouldBeTrue();
        using var read = fixture.BeginScope();
        var attempt = await read.Resolve<CodeSpaceDbContext>().WorkflowRunModelCallAttempt.AsNoTracking().SingleAsync(a => a.WorkflowRunId == scenario.RunId);
        attempt.EffectiveModel.ShouldBeNull("a format-character-forged wire model must degrade to unknown, never persist verbatim into the accounting trail");
        (await read.Resolve<CodeSpaceDbContext>().BudgetReservation.SingleAsync(r => r.WorkflowRunId == scenario.RunId)).SettledUsd.ShouldBeNull();
    }

    private async Task<object> CensusAsync(Scenario scenario, ProtocolHandler handler, StructuredLLMCompletion result)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var records = await db.WorkflowRunRecord.AsNoTracking().Where(r => r.RunId == scenario.RunId && r.RecordType.StartsWith("interaction.")).ToArrayAsync();
        var reservations = await db.BudgetReservation.AsNoTracking().Where(r => r.WorkflowRunId == scenario.RunId).ToArrayAsync();
        return new
        {
            physicalPosts = handler.Admissions.Count, beforeSendClaimCounts = handler.Admissions.Select(x => x.Distinct().Count()).ToArray(),
            recordTypes = records.Select(r => r.RecordType).OrderBy(x => x).ToArray(), logicalCorrelationIds = records.Select(r => r.CorrelationId).Distinct().Count(),
            reservationStates = reservations.Select(r => new { r.State, r.ReservedUsd, r.SettledUsd }).ToArray(),
            returnedUsage = result.Usage, failedOverCandidates = result.FailedOver.Count,
        };
    }

    private async Task<Guid[]> ReservationIdsAsync(Scenario scenario)
    {
        using var scope = fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().BudgetReservation.AsNoTracking().Where(r => r.TeamId == scenario.TeamId && r.WorkflowRunId == scenario.RunId).Select(r => r.Id).ToArrayAsync();
    }

    private async Task<Scenario> SeedAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture, inProcessPool: false);
        using var scope = fixture.BeginScopeAs(userId, teamId);
        var workflowId = await scope.Resolve<MediatR.IMediator>().Send(new CodeSpace.Messages.Commands.Workflows.CreateWorkflowCommand { Name = "physical-accounting-fixture", Definition = WorkflowsTestSeed.MinimalDefinition(), Activations = Array.Empty<CodeSpace.Messages.Commands.Workflows.WorkflowActivationInput>(), Enabled = true });
        return new Scenario(await WorkflowsTestSeed.SeedManualRunAsync(fixture, workflowId, teamId), teamId);
    }

    private static IDisposable Push(ILifetimeScope scope, Scenario scenario, decimal cap = 100m) => LlmCallContext.Push(new LlmCallScope(scenario.RunId, scenario.TeamId, "n", "i", "protocol-audit", scope.Resolve<IRunRecordLogger>(), scope.Resolve<IArtifactOffloader>(), scope.Resolve<IBudgetLedger>(), cap, new Dictionary<string, ModelPrice> { ["fixture-model"] = new() { InputPerMillionUsd = 1m, OutputPerMillionUsd = 1m } }));
    private static ServiceProvider HttpServices(ProtocolHandler handler, Action<PhysicalLlmObservationOptions>? configure = null, Action<IServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddLlmHttpClients();
        if (configure is not null) services.Configure(configure);
        foreach (var name in LlmHttpClientRegistration.ClientNames) services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => handler);
        configureServices?.Invoke(services);
        return services.BuildServiceProvider();
    }
    private static IStructuredLLMClient Decorated(string provider, IHttpClientFactory factory) => new RecordingStructuredLLMClientDecorator(provider == "Anthropic" ? new AnthropicClient(factory) : new OpenAiClient(factory));
    private static ResolvedModelCredential Credential(string provider) => new() { Provider = provider, ApiKey = "synthetic-fixture-key", BaseUrl = "https://physical-accounting.invalid/v1" };
    private static StructuredLLMCompletionRequest Request(string provider, string path) => new()
    {
        Model = "fixture-model", Credential = Credential(provider), MaxOutputTokens = 100, SystemPrompt = "return data", UserPrompt = "approve the synthetic fixture",
        JsonSchema = JsonSerializer.SerializeToElement(path == "schema-reask" ? new { type = "object", required = new[] { "approved" } } : (object)new { type = "object" }),
        ResponseValidator = path == "consumer-reask" ? json => json.TryGetProperty("approved", out var approved) && approved.ValueKind == JsonValueKind.True ? [] : ["approved must be true"] : null,
    };
    private static Reply[] Replies(string provider, string path) => path switch
    {
        "forced-fallback" => [Text(provider), AcceptedText(provider)],
        "tool-rejected" => [new Reply(HttpStatusCode.BadRequest, "{\"error\":\"tool protocol unsupported\"}"), AcceptedText(provider)],
        "parse-reask" => [Text(provider), Text(provider), Success(provider)],
        "schema-reask" or "consumer-reask" => [Structured(provider, "{}", 100), Success(provider)],
        "transport-retry" => [new Reply(HttpStatusCode.ServiceUnavailable, "{\"error\":\"synthetic transient fault\"}"), Success(provider)],
        "pool-failover" => [new Reply(HttpStatusCode.ServiceUnavailable, "{}"), new Reply(HttpStatusCode.ServiceUnavailable, "{}"), new Reply(HttpStatusCode.ServiceUnavailable, "{}"), Success(provider == "Anthropic" ? "OpenAI" : "Anthropic")],
        _ => throw new ArgumentOutOfRangeException(nameof(path)),
    };
    private static Reply Success(string provider) => Structured(provider, "{\"approved\":true}", 10);
    private static Reply Text(string provider) => new(HttpStatusCode.OK, provider == "Anthropic"
        ? JsonSerializer.Serialize(new { model = "fixture-model", content = new[] { new { type = "text", text = "no JSON result" } }, usage = new { input_tokens = 100, output_tokens = 100 } })
        : JsonSerializer.Serialize(new { model = "fixture-model", choices = new[] { new { message = new { content = "no JSON result" } } }, usage = new { prompt_tokens = 100, completion_tokens = 100 } }));
    private static Reply AcceptedText(string provider) => new(HttpStatusCode.OK, provider == "Anthropic"
        ? JsonSerializer.Serialize(new { model = "fixture-model", content = new[] { new { type = "text", text = "{\"approved\":true}" } }, usage = new { input_tokens = 10, output_tokens = 5 } })
        : JsonSerializer.Serialize(new { model = "fixture-model", choices = new[] { new { message = new { content = "{\"approved\":true}" } } }, usage = new { prompt_tokens = 10, completion_tokens = 5 } }));
    private static Reply Structured(string provider, string json, int input) => new(HttpStatusCode.OK, provider == "Anthropic"
        ? "{\"model\":\"fixture-model\",\"content\":[{\"type\":\"tool_use\",\"name\":\"respond\",\"input\":" + json + "}],\"usage\":{\"input_tokens\":" + input + ",\"output_tokens\":5}}"
        : "{\"model\":\"fixture-model\",\"choices\":[{\"message\":{\"tool_calls\":[{\"function\":{\"name\":\"respond\",\"arguments\":" + JsonSerializer.Serialize(json) + "}}]}}],\"usage\":{\"prompt_tokens\":" + input + ",\"completion_tokens\":5}}");

    private sealed record Scenario(Guid RunId, Guid TeamId);
    private sealed record Reply(HttpStatusCode Status, string Body, bool LostResponse = false, Func<HttpContent>? Content = null);
    private sealed class ProtocolHandler(string provider, IReadOnlyList<Reply> replies, Func<Task<Guid[]>> admissions) : HttpMessageHandler
    {
        public List<Guid[]> Admissions { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Method.ShouldBe(HttpMethod.Post);
            await request.Content!.ReadAsByteArrayAsync(cancellationToken); // the protocol peer has received this POST before any simulated loss
            Admissions.Add(await admissions());
            if (Admissions.Count > replies.Count) throw new InvalidOperationException("unexpected physical POST in " + provider);
            var reply = replies[Admissions.Count - 1];
            if (reply.LostResponse) throw new HttpRequestException("synthetic response loss after request receipt");
            var response = new HttpResponseMessage(reply.Status) { Content = reply.Content?.Invoke() ?? new StringContent(reply.Body, Encoding.UTF8, "application/json") };
            if (reply.Status == HttpStatusCode.ServiceUnavailable) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        }
    }
}
