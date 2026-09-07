using System.Net;
using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Review;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Llm.OpenAi;
using CodeSpace.Core.Services.Workflows.Llm.Anthropic;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ProviderBudgetAccountingFlowTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(null)]
    [InlineData("{\"prompt_tokens\":5}")]
    [InlineData("{\"prompt_tokens\":-1,\"completion_tokens\":2}")]
    public async Task A_real_critic_with_missing_or_invalid_usage_holds_its_budget_and_cannot_bill_another_review(string? usage)
    {
        var scenario = await SeedAsync();
        using var response = new ReplyHandler((HttpStatusCode.OK, Response(usage)));
        var verdict = await ReviewAsync(scenario, response, 100m);
        verdict.Failed.ShouldBeFalse("the accepted review still reaches its caller");
        response.Calls.ShouldBe(1);

        var row = (await ReservationsAsync(scenario)).ShouldHaveSingleItem();
        row.Kind.ShouldBe("llm:critic.review", "the real critic relabel preserves the ambient budget");
        row.State.ShouldBe(BudgetReservationStates.Indeterminate);
        row.SettledUsd.ShouldBeNull();
        row.ReservedUsd.ShouldBeGreaterThan(0m);
        var completed = await CompletedAsync(scenario);
        InteractionSpend.From(completed, Prices()).CostUsd.ShouldBeNull();

        using var refused = new ReplyHandler((HttpStatusCode.OK, Response("{\"prompt_tokens\":1,\"completion_tokens\":1}")));
        var next = await ReviewAsync(scenario, refused, row.ReservedUsd);
        next.Failed.ShouldBeTrue();
        next.Rationale.ShouldContain(nameof(LlmBudgetExceededException));
        refused.Calls.ShouldBe(0, "the retained unknown claim is consumed by real provider admission");
    }

    [Fact]
    public async Task A_real_critic_settles_the_observed_model_price_and_provider_reported_zero_remains_a_known_zero()
    {
        var scenario = await SeedAsync();
        using var response = new ReplyHandler((HttpStatusCode.OK, Response("{\"prompt_tokens\":10,\"completion_tokens\":5}")));
        (await ReviewAsync(scenario, response, 100m)).Failed.ShouldBeFalse();
        var row = (await ReservationsAsync(scenario)).ShouldHaveSingleItem();
        row.SettledUsd.ShouldBe(0.00004m, "the responding model is priced at 2/4 per million, not the requested model's 1/1");
        row.State.ShouldBe(BudgetReservationStates.Settled);
        InteractionSpend.From(await CompletedAsync(scenario), Prices()).CostUsd.ShouldBe(row.SettledUsd);

        var zero = await SeedAsync();
        using var zeroResponse = new ReplyHandler((HttpStatusCode.OK, Response("{\"prompt_tokens\":0,\"completion_tokens\":0}")));
        (await ReviewAsync(zero, zeroResponse, 100m)).Failed.ShouldBeFalse();
        var free = (await ReservationsAsync(zero)).ShouldHaveSingleItem();
        free.State.ShouldBe(BudgetReservationStates.Settled);
        free.SettledUsd.ShouldBe(0m, "an explicit complete provider-reported zero is different from missing usage");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_malformed_physical_post_cannot_be_erased_by_the_successful_reasks_usage(bool anthropic)
    {
        var scenario = await SeedAsync(anthropic);
        var malformed = anthropic
            ? JsonSerializer.Serialize(new { model = "observed-model", content = new[] { new { type = "text", text = "no structured result" } }, usage = new { input_tokens = 100, output_tokens = 100 } })
            : JsonSerializer.Serialize(new { model = "observed-model", choices = new[] { new { message = new { content = "no structured result" } } }, usage = new { prompt_tokens = 100, completion_tokens = 100 } });
        var accepted = anthropic ? AnthropicResponse() : Response("{\"prompt_tokens\":10,\"completion_tokens\":5}");
        using var response = new ReplyHandler((HttpStatusCode.OK, malformed), (HttpStatusCode.OK, malformed), (HttpStatusCode.OK, accepted));
        (await ReviewAsync(scenario, response, 100m)).Failed.ShouldBeFalse();
        response.Calls.ShouldBe(3);
        var row = (await ReservationsAsync(scenario)).ShouldHaveSingleItem();
        row.State.ShouldBe(BudgetReservationStates.Indeterminate);
        row.SettledUsd.ShouldBeNull("the final POST's usage is only a known subtotal of this logical provider call");
        var completed = await CompletedAsync(scenario);
        JsonDocument.Parse(completed.PayloadJson).RootElement.GetProperty("usage").GetProperty("isPartial").GetBoolean().ShouldBeTrue();
        InteractionSpend.From(completed, Prices()).CostUsd.ShouldBeNull("the persisted cost fold must see the same uncertainty as admission");
    }

    [Fact]
    public async Task A_failed_over_provider_keeps_the_failed_hops_claim_and_the_successor_spend()
    {
        var scenario = await SeedAsync();
        using var firstHttp = new ReplyHandler((HttpStatusCode.ServiceUnavailable, "{\"error\":{\"message\":\"upstream unavailable\"}}"));
        using var secondHttp = new ReplyHandler((HttpStatusCode.OK, Response("{\"prompt_tokens\":10,\"completion_tokens\":5}")));
        using var scope = fixture.BeginScope();
        var pick = (await scope.Resolve<IModelPoolSelector>().ResolveByRowIdAsync(scenario.TeamId, scenario.ModelRowId, CancellationToken.None)).ShouldNotBeNull();
        var client = new FailoverStructuredClient(new[]
        {
            ((IStructuredLLMClient)Decorated(firstHttp), pick),
            ((IStructuredLLMClient)Decorated(secondHttp), pick),
        });
        using (PushScope(scope, scenario, 100m))
        {
            var result = await client.CompleteStructuredAsync(Request(pick.Credential), CancellationToken.None);
            result.FailedOver.Count.ShouldBe(1);
            result.Model.ShouldBe("observed-model");
        }

        var rows = await ReservationsAsync(scenario);
        rows.Count.ShouldBe(2, "each failover candidate passes through the production recording/budget decorator");
        rows.Count(r => r.State == BudgetReservationStates.Indeterminate && r.SettledUsd == null).ShouldBe(1);
        rows.Single(r => r.State == BudgetReservationStates.Settled).SettledUsd.ShouldBe(0.00004m);
        var committed = await scope.Resolve<IBudgetLedger>().CommittedUsdAsync(scenario.RunId, scenario.TeamId, CancellationToken.None);
        committed.ShouldBe(rows.Single(r => r.State == BudgetReservationStates.Indeterminate).ReservedUsd + 0.00004m);
        firstHttp.Calls.ShouldBe(1);
        secondHttp.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_plain_provider_completion_with_no_usage_uses_the_same_unknown_cost_accounting()
    {
        var scenario = await SeedAsync();
        using var response = new ReplyHandler((HttpStatusCode.OK, "{\"model\":\"observed-model\",\"choices\":[{\"message\":{\"content\":\"answer\"}}]}"));
        using var scope = fixture.BeginScope();
        var pick = (await scope.Resolve<IModelPoolSelector>().ResolveByRowIdAsync(scenario.TeamId, scenario.ModelRowId, CancellationToken.None)).ShouldNotBeNull();
        using (PushScope(scope, scenario, 100m))
            (await Decorated(response).CompleteAsync(new LLMCompletionRequest { Model = pick.ModelId, SystemPrompt = "s", UserPrompt = "u", MaxOutputTokens = 100, Credential = pick.Credential }, CancellationToken.None)).Text.ShouldBe("answer");
        var row = (await ReservationsAsync(scenario)).ShouldHaveSingleItem();
        row.State.ShouldBe(BudgetReservationStates.Indeterminate);
        row.SettledUsd.ShouldBeNull();
    }

    [Fact]
    public async Task Concurrent_real_provider_calls_reserve_before_their_HTTP_requests_and_settle_after_release()
    {
        var scenario = await SeedAsync();
        using var resolve = fixture.BeginScope();
        var pick = (await resolve.Resolve<IModelPoolSelector>().ResolveByRowIdAsync(scenario.TeamId, scenario.ModelRowId, CancellationToken.None)).ShouldNotBeNull();
        var request = Request(pick.Credential);
        var estimate = LlmBudgetGuard.EstimateUsd(request.Model, request.SystemPrompt, request.UserPrompt, request.MaxOutputTokens, Prices())!.Value;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admissions = System.Threading.Channels.Channel.CreateUnbounded<bool>();
        var workers = Enumerable.Range(0, 8).Select(async _ =>
        {
            using var scope = fixture.BeginScope();
            using var http = new ReplyHandler((HttpStatusCode.OK, Response("{\"prompt_tokens\":10,\"completion_tokens\":5}")))
            {
                BeforeReply = release.Task,
                RequestStarted = () => admissions.Writer.TryWrite(true),
            };
            using var ambient = PushScope(scope, scenario, 3 * estimate);
            try
            {
                await Decorated(http).CompleteStructuredAsync(request, CancellationToken.None);
                return true;
            }
            catch (LlmBudgetExceededException)
            {
                http.Calls.ShouldBe(0);
                admissions.Writer.TryWrite(false);
                return false;
            }
        }).ToArray();

        try
        {
            var entered = 0;
            for (var i = 0; i < 8; i++) if (await admissions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20))) entered++;
            entered.ShouldBe(3, "five callers are refused while three real HTTP requests still hold their claims");
            var held = await ReservationsAsync(scenario);
            held.Count.ShouldBe(3);
            held.ShouldAllBe(r => r.State == BudgetReservationStates.Reserved && r.SettledUsd == null);
        }
        finally { release.TrySetResult(); await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(20)); }
        (await Task.WhenAll(workers)).Count(accepted => accepted).ShouldBe(3);
        var rows = await ReservationsAsync(scenario);
        rows.ShouldAllBe(r => r.State == BudgetReservationStates.Settled && r.SettledUsd == 0.00004m);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_internal_reasks_with_different_observed_models_cannot_be_priced_as_one_model(bool anthropic)
    {
        var scenario = await SeedAsync(anthropic);
        var invalid = anthropic
            ? JsonSerializer.Serialize(new { model = "earlier-model", content = new[] { new { type = "tool_use", name = "respond", input = new { rationale = "missing required verdict fields" } } }, usage = new { input_tokens = 100, output_tokens = 100 } })
            : JsonSerializer.Serialize(new { model = "earlier-model", choices = new[] { new { message = new { tool_calls = new[] { new { function = new { name = "respond", arguments = "{\"rationale\":\"missing required verdict fields\"}" } } } } } }, usage = new { prompt_tokens = 100, completion_tokens = 100 } });
        using var http = new ReplyHandler((HttpStatusCode.OK, invalid), (HttpStatusCode.OK, anthropic ? AnthropicResponse() : Response("{\"prompt_tokens\":10,\"completion_tokens\":5}")));
        (await ReviewAsync(scenario, http, 100m)).Failed.ShouldBeFalse();
        http.Calls.ShouldBe(2);
        var row = (await ReservationsAsync(scenario)).ShouldHaveSingleItem();
        row.SettledUsd.ShouldBeNull("an earlier model's tokens cannot be billed at the successor model's rate");
        row.State.ShouldBe(BudgetReservationStates.Indeterminate);
        InteractionSpend.From(await CompletedAsync(scenario), Prices()).CostUsd.ShouldBeNull();
    }

    private async Task<CriticVerdict> ReviewAsync(Scenario scenario, ReplyHandler response, decimal cap)
    {
        using var scope = fixture.BeginScope();
        var registry = new LLMClientRegistry(new[] { Decorated(response, scenario.Anthropic) });
        var critic = new LlmStructuredCritic(registry, scope.Resolve<IModelPoolSelector>(), scope.Resolve<ILogger<LlmStructuredCritic>>());
        using var ambient = PushScope(scope, scenario, cap);
        return await critic.ReviewAsync(new CriticRequest { Mode = ReviewMode.Gate, ArtifactKind = "test artifact", Artifact = "finished result", Goal = "review the result" }, scenario.TeamId, scenario.ModelRowId, CancellationToken.None);
    }

    private static IDisposable PushScope(ILifetimeScope scope, Scenario scenario, decimal cap) => LlmCallContext.Push(new LlmCallScope(scenario.RunId, scenario.TeamId, "sup", "turn1", "supervisor.decision", scope.Resolve<IRunRecordLogger>(), scope.Resolve<IArtifactOffloader>(), scope.Resolve<IBudgetLedger>(), cap, Prices()));
    private static RecordingStreamingStructuredLLMClientDecorator Decorated(ReplyHandler response, bool anthropic = false) => new(anthropic ? new AnthropicClient(new HttpFactory(response)) : new OpenAiClient(new HttpFactory(response)));
    private static IReadOnlyDictionary<string, ModelPrice> Prices() => new Dictionary<string, ModelPrice>
    {
        ["requested-model"] = new() { InputPerMillionUsd = 1m, OutputPerMillionUsd = 1m },
        ["observed-model"] = new() { InputPerMillionUsd = 2m, OutputPerMillionUsd = 4m },
    };

    private static StructuredLLMCompletionRequest Request(ResolvedModelCredential credential) => new() { Model = "requested-model", SystemPrompt = "s", UserPrompt = "u", MaxOutputTokens = 100, JsonSchema = JsonSerializer.SerializeToElement(new { type = "object" }), Credential = credential };
    private static string Response(string? usage) => "{\"model\":\"observed-model\",\"choices\":[{\"message\":{\"tool_calls\":[{\"function\":{\"name\":\"respond\",\"arguments\":\"{\\\"approved\\\":true,\\\"score\\\":9,\\\"issues\\\":[],\\\"rationale\\\":\\\"ready\\\"}\"}}]}}]" + (usage == null ? "" : ",\"usage\":" + usage) + "}";

    private static string AnthropicResponse() => JsonSerializer.Serialize(new
    {
        model = "observed-model",
        content = new[] { new { type = "tool_use", name = "respond", input = new { approved = true, score = 9, issues = Array.Empty<object>(), rationale = "ready" } } },
        usage = new { input_tokens = 10, output_tokens = 5 },
    });

    private async Task<Scenario> SeedAsync(bool anthropic = false)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture, inProcessPool: false);
        using var scope = fixture.BeginScopeAs(userId, teamId);
        var workflowId = await scope.Resolve<MediatR.IMediator>().Send(new CodeSpace.Messages.Commands.Workflows.CreateWorkflowCommand { Name = "budget-" + Guid.NewGuid().ToString("N"), Definition = WorkflowsTestSeed.MinimalDefinition(), Activations = Array.Empty<CodeSpace.Messages.Commands.Workflows.WorkflowActivationInput>(), Enabled = true });
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(fixture, workflowId, teamId);
        var db = scope.Resolve<CodeSpaceDbContext>();
        var credentialId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential { Id = credentialId, TeamId = teamId, Provider = anthropic ? "Anthropic" : "OpenAI", DisplayName = "budget test", EncryptedApiKey = scope.Resolve<IPayloadEncryptor>().Encrypt("test-key"), BaseUrl = "https://budget.test/v1", Status = CredentialStatus.Active });
        var modelRowId = Guid.NewGuid();
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = modelRowId, ModelCredentialId = credentialId, ModelId = "requested-model", Source = ModelSource.Manual, Enabled = true, InputUsdPerMillion = 1m, OutputUsdPerMillion = 1m });
        await db.SaveChangesAsync();
        return new Scenario(runId, teamId, modelRowId, anthropic);
    }

    private async Task<List<BudgetReservation>> ReservationsAsync(Scenario scenario)
    {
        using var scope = fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().BudgetReservation.AsNoTracking().Where(r => r.WorkflowRunId == scenario.RunId).ToListAsync();
    }

    private async Task<WorkflowRunRecord> CompletedAsync(Scenario scenario)
    {
        using var scope = fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking().SingleAsync(r => r.RunId == scenario.RunId && r.RecordType == "interaction.completed");
    }

    private sealed record Scenario(Guid RunId, Guid TeamId, Guid ModelRowId, bool Anthropic);
    private sealed class HttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class ReplyHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new(responses);
        public int Calls { get; private set; }
        public Task? BeforeReply { get; init; }
        public Action? RequestStarted { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            RequestStarted?.Invoke();
            if (BeforeReply is { } gate) await gate.WaitAsync(cancellationToken);
            if (_responses.Count == 0) throw new InvalidOperationException("unexpected additional provider POST");
            var response = _responses.Dequeue();
            return new HttpResponseMessage(response.Status) { Content = new StringContent(response.Body, Encoding.UTF8, "application/json") };
        }
    }
}
