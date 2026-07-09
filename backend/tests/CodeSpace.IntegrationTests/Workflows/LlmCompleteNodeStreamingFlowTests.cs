using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The END-TO-END proof that a real <c>llm.complete</c> node drives the streaming pipeline: a LARGE output (above the
/// streaming gate) routes through the streaming sibling → the REAL recording decorator → coalesced <c>interaction.delta</c>
/// rows on the REAL ledger, folded back into the node's text output; a SMALL output stays buffered and records NO delta;
/// and a mid-stream fault records <c>interaction.failed</c> with the partial deltas that arrived — <c>completed</c> stays
/// the authority, never landing on a failed call. The node's own observability is NoOp here so the assertion isolates the
/// interaction.* records the client seam writes (the delta source), not the external_call.* plumbing.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class LlmCompleteNodeStreamingFlowTests
{
    private readonly PostgresFixture _fixture;

    public LlmCompleteNodeStreamingFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_large_output_node_streams_and_persists_ordered_coalesced_interaction_delta_rows()
    {
        var (rows, textOutput) = await RunNodeAsync(maxTokens: 60000, new LargeStreamingClient());

        rows[0].RecordType.ShouldBe(WorkflowRunRecordTypes.InteractionStarted);
        rows[^1].RecordType.ShouldBe(WorkflowRunRecordTypes.InteractionCompleted, "the completed record lands last — the whole authoritative output");

        var deltas = rows.Where(r => r.RecordType == WorkflowRunRecordTypes.InteractionDelta).ToList();
        deltas.Count.ShouldBeGreaterThanOrEqualTo(2, "a large streamed node output persists coalesced delta rows");
        deltas.Select(d => JsonDocument.Parse(d.PayloadJson).RootElement.GetProperty("ordinal").GetInt32())
            .ShouldBe(Enumerable.Range(0, deltas.Count), "delta ordinals are monotonic from 0 in ledger Sequence order");
        deltas.ShouldAllBe(d => d.CorrelationId == rows[0].CorrelationId, "every delta shares the started/completed correlation id");

        textOutput.ShouldBe(new string('x', 600), "the streamed deltas fold into the node's whole text output");
    }

    [Fact]
    public async Task A_small_output_node_stays_buffered_and_records_no_delta()
    {
        var (rows, textOutput) = await RunNodeAsync(maxTokens: 2048, new LargeStreamingClient());

        rows.Select(r => r.RecordType).ShouldBe(new[] { WorkflowRunRecordTypes.InteractionStarted, WorkflowRunRecordTypes.InteractionCompleted },
            "a small output takes the buffered path — started + completed, NO delta noise");
        textOutput.ShouldBe("buffered");
    }

    [Fact]
    public async Task A_faulted_stream_leaves_failed_authoritative_with_any_partial_deltas()
    {
        var runId = await RunNodeExpectingFaultAsync(new FaultyStreamingClient());

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var rows = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.RecordType.StartsWith("interaction."))
            .OrderBy(r => r.Sequence).ToListAsync();

        rows[0].RecordType.ShouldBe(WorkflowRunRecordTypes.InteractionStarted);
        rows[^1].RecordType.ShouldBe(WorkflowRunRecordTypes.InteractionFailed, "a mid-stream fault ends the call as FAILED — the authoritative terminal event");
        rows.ShouldNotContain(r => r.RecordType == WorkflowRunRecordTypes.InteractionCompleted, "completed never lands on a faulted call");
        // Partial deltas MAY exist (whatever arrived before the drop) — they're a progressive view, never the authority.
        rows.Count(r => r.RecordType == WorkflowRunRecordTypes.InteractionDelta).ShouldBeGreaterThanOrEqualTo(1);
    }

    // ─── Harness ────────────────────────────────────────────────────────────────

    private async Task<(IReadOnlyList<WorkflowRunRecord> Rows, string? TextOutput)> RunNodeAsync(int maxTokens, ILLMClient inner)
    {
        var runId = await DriveNodeAsync(maxTokens, inner, expectFault: false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var rows = await db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.RecordType.StartsWith("interaction."))
            .OrderBy(r => r.Sequence).ToListAsync();

        return (rows, _lastText);
    }

    private async Task<Guid> RunNodeExpectingFaultAsync(ILLMClient inner) => await DriveNodeAsync(60000, inner, expectFault: true);

    private string? _lastText;

    private async Task<Guid> DriveNodeAsync(int maxTokens, ILLMClient inner, bool expectFault)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        using var scope = _fixture.BeginScope();
        var logger = scope.Resolve<IRunRecordLogger>();
        var offloader = scope.Resolve<IArtifactOffloader>();

        // The DECORATED streaming client — the exact production wrapping (records interaction.*).
        var decorated = new RecordingStreamingStructuredLLMClientDecorator(inner);
        var node = new LlmCompleteNode(new StubRegistry(decorated), new StubScopeFactory(StubPoolSelector.WithModel(teamId)));

        var context = BuildContext(teamId, maxTokens);

        // Push the LlmCallContext scope the engine pushes per node, so the recording decorator fires interaction.*.
        using (LlmCallContext.Push(new LlmCallScope(runId, teamId, "llm1", "llm1#0", "llm.complete", logger, offloader)))
        {
            if (expectFault)
            {
                await Should.ThrowAsync<InvalidOperationException>(() => node.RunAsync(context, CancellationToken.None));
            }
            else
            {
                var result = await node.RunAsync(context, CancellationToken.None);
                result.Status.ShouldBe(NodeStatus.Success);
                _lastText = result.Outputs["text"].GetString();
            }
        }

        return runId;
    }

    private static NodeRunContext BuildContext(Guid teamId, int maxTokens) => new()
    {
        Inputs = new Dictionary<string, JsonElement> { ["userPrompt"] = JsonSerializer.SerializeToElement("go") },
        Config = new Dictionary<string, JsonElement> { ["maxTokens"] = JsonSerializer.SerializeToElement(maxTokens) },
        RawInputs = JsonDocument.Parse("{}").RootElement,
        RawConfig = JsonDocument.Parse("{}").RootElement,
        Scope = new NodeRunScope
        {
            Trigger = new Dictionary<string, JsonElement>(),
            Sys = new Dictionary<string, JsonElement> { [SystemScopeKeys.TeamId] = JsonSerializer.SerializeToElement(teamId.ToString()) },
        },
        Logger = NullLogger.Instance,
        Observability = NodeObservability.NoOp,   // isolate interaction.* (from the client seam) — not external_call.*
    };

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<MediatR.IMediator>();
        return await mediator.Send(new CodeSpace.Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "llmstream-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<CodeSpace.Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    // ─── Stubs (mirror the LlmCompleteNode unit-test doubles) ─────────────────────

    private sealed class StubRegistry : ILLMClientRegistry
    {
        private readonly ILLMClient _client;
        public StubRegistry(ILLMClient client) { _client = client; }
        public ILLMClient Resolve(string provider) => _client;
        public IReadOnlyList<ILLMClient> All => new[] { _client };
    }

    private sealed class StubPoolSelector : IModelPoolSelector
    {
        private readonly ModelPoolPick _pick;
        private StubPoolSelector(ModelPoolPick pick) { _pick = pick; }
        public static StubPoolSelector WithModel(Guid teamId) => new(new ModelPoolPick { ModelId = "claude-sonnet-4-5", Credential = new ResolvedModelCredential { Provider = "Anthropic", ApiKey = "k" } });
        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) => Task.FromResult<ModelPoolPick?>(_pick);
        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) => Task.FromResult<ModelPoolPick?>(_pick);
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult<ModelDispatchRef?>(null);
        public Task<IReadOnlyList<PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PoolModelInfo>>(Array.Empty<PoolModelInfo>());
        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<Guid?> ResolvePinnedBrainRowIdAsync(Guid teamId, Guid modelCredentialModelId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<string?> ResolveTeamDefaultProviderAsync(Guid teamId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class StubScopeFactory : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        private readonly IModelPoolSelector _selector;
        public StubScopeFactory(IModelPoolSelector selector) { _selector = selector; }
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) => serviceType == typeof(IModelPoolSelector) ? _selector : null;
        public void Dispose() { }
    }

    private sealed class LargeStreamingClient : ILLMClient, IStructuredLLMClient, IStreamingLLMClient
    {
        public string Provider => "Anthropic";
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) => Task.FromResult(new LLMCompletion { Text = "buffered", Model = "claude-sonnet-4-5", Usage = new() { InputTokens = 1, OutputTokens = 1 } });
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct) => Task.FromResult(new StructuredLLMCompletion { Json = JsonSerializer.SerializeToElement(new { }), Model = "claude-sonnet-4-5" });

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LLMCompletionRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new LlmStreamEvent.Meta(Model: "claude-sonnet-4-5", InputTokens: 7);
            for (var i = 0; i < 600; i++) yield return new LlmStreamEvent.TextDelta("x");   // > 2× the 256-char coalesce threshold → ≥2 delta rows
            yield return new LlmStreamEvent.Meta(OutputTokens: 4, FinishReason: "end_turn");
            await Task.CompletedTask;
        }
    }

    private sealed class FaultyStreamingClient : ILLMClient, IStructuredLLMClient, IStreamingLLMClient
    {
        public string Provider => "Anthropic";
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) => Task.FromResult(new LLMCompletion { Text = "buffered", Model = "claude-sonnet-4-5" });
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct) => Task.FromResult(new StructuredLLMCompletion { Json = JsonSerializer.SerializeToElement(new { }), Model = "claude-sonnet-4-5" });

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LLMCompletionRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new LlmStreamEvent.Meta(Model: "claude-sonnet-4-5", InputTokens: 7);
            for (var i = 0; i < 300; i++) yield return new LlmStreamEvent.TextDelta("x");   // enough to flush ≥1 coalesced delta before the drop
            await Task.CompletedTask;
            throw new InvalidOperationException("the gateway dropped mid-stream");
        }
    }
}
