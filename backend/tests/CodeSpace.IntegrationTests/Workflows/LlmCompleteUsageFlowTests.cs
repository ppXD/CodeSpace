using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// End-to-end-through-Postgres proof of the P1a accounting headline: an <c>llm.complete</c> node run by the REAL
/// engine persists the provider's finish reason AND a DERIVED USD cost onto the external_call.completed ledger row —
/// the in-process LLM plane's accounting surface, which previously carried only raw token counts and never priced its
/// own calls (cost was agent-plane-only). The deterministic synth fake (root-registered under "TestSynth") returns a
/// fixed usage (17 in / 19 out / "stop"); the pool seeds a PRICED model (claude-opus-4-8 = $5/$25 per M) so the node
/// computes, persists, and this test re-reads the exact cost. Mirrors <see cref="ExternalCallTraceFlowTests"/>'s
/// ledger-shape pattern for the http node.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class LlmCompleteUsageFlowTests
{
    private const string PricedModel = "claude-opus-4-8";   // $5/M input + $25/M output (AgentCostPricing defaults)

    private readonly PostgresFixture _fixture;

    public LlmCompleteUsageFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Llm_complete_persists_finish_reason_and_derived_cost_on_the_ledger()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // A priced pool model UNDER THE SYNTH FAKE'S PROVIDER so the node resolves it and the cost is computable.
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, PricedModel, provider: DeterministicSynthLlmClient.ProviderTag);

        var workflowId = await CreateLlmWorkflowAsync(teamId, userId, PricedModel);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var completed = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.ExternalCallCompleted)
            .SingleAsync();

        var payload = JsonDocument.Parse(completed.PayloadJson).RootElement.GetProperty("response_payload");

        payload.GetProperty("input_tokens").GetInt32().ShouldBe(17);
        payload.GetProperty("output_tokens").GetInt32().ShouldBe(19);
        payload.GetProperty("finish_reason").GetString().ShouldBe("stop",
            "the provider stop reason MUST persist on the ledger so an operator triaging a run sees WHY it stopped without the live logs");

        // claude-opus-4-8 = $5/M in + $25/M out → 17*5/1e6 + 19*25/1e6 = 0.00056. DERIVED at the node, persisted, re-read.
        payload.GetProperty("cost_usd").GetDecimal().ShouldBe(0.00056m,
            "the in-process LLM plane now prices its OWN calls end-to-end — the agent-plane-only cost gap is closed");
    }

    [Fact]
    public async Task Ledger_keeps_finish_reason_and_a_present_null_cost_for_an_unpriced_model()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        // An UNPRICED custom-gateway model → cost can't be derived, but the key MUST still be PRESENT (as null), never
        // dropped — so a consumer can tell "unpriced" from "field absent", and a future serializer that omits nulls
        // can't silently strip it undetected.
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "metis-coder-max", provider: DeterministicSynthLlmClient.ProviderTag);

        var workflowId = await CreateLlmWorkflowAsync(teamId, userId, "metis-coder-max");
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var completed = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.ExternalCallCompleted)
            .SingleAsync();

        var payload = JsonDocument.Parse(completed.PayloadJson).RootElement.GetProperty("response_payload");

        payload.GetProperty("finish_reason").GetString().ShouldBe("stop", "finish reason persists even when the model is unpriced");

        payload.TryGetProperty("cost_usd", out var cost).ShouldBeTrue("cost_usd MUST remain a present key even when null");
        cost.ValueKind.ShouldBe(JsonValueKind.Null, "an unpriced model yields a present-but-null cost on the ledger — never a dropped key or a bogus zero");
    }

    private async Task<Guid> CreateLlmWorkflowAsync(Guid teamId, Guid userId, string pinnedModel)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<MediatR.IMediator>();

        // PIN the model explicitly: SeedTeamAsync also seeds a "<tag>-model" per fake provider, so the team's pool
        // holds two models under this tag. An UNPINNED pick orders by model id — a Postgres-collation-dependent
        // tie-break (case-sensitive C-locale in CI vs case-insensitive locally would pick differently). Pinning makes
        // the selection deterministic + independent of what else the seed put in the pool.
        var def = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "gen", TypeKey = "llm.complete",
                        Config = WorkflowsTestSeed.Json($$"""{"provider":"{{DeterministicSynthLlmClient.ProviderTag}}","model":"{{pinnedModel}}"}"""),
                        Inputs = WorkflowsTestSeed.Json("""{"userPrompt":"reduce these results"}""") },
                new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "gen" },
                new() { From = "gen", To = "end" },
            },
        };

        return await mediator.Send(new CodeSpace.Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "llm-usage-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = def,
            Activations = new List<CodeSpace.Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }
}
