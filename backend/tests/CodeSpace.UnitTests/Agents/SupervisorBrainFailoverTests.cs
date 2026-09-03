using System.Text.Json;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the decision lane's L4 fold. Pins: with no alternates the decider keeps the RAW client (same instance —
/// byte-identical, and exactly what an operator-pinned brain always gets); with alternates it wraps [current,
/// alternates…] in a failover client with the current brain FIRST (the selector's own precedence, never a reorder);
/// the per-decision candidate cap is pinned so a turn never decrypts the whole pool's credentials.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorBrainFailoverTests
{
    [Fact]
    public void No_alternates_keeps_the_raw_client_byte_identically()
    {
        var current = new Stub("Anthropic");

        LlmSupervisorDecider.BuildBrainClient(current, Pick("Anthropic"), Array.Empty<(IStructuredLLMClient, ModelPoolPick)>())
            .ShouldBeSameAs(current, "a one-brain pool (or a pinned brain, which never receives alternates) must see no wrapper at all");
    }

    [Fact]
    public void Alternates_wrap_the_current_brain_first()
    {
        var current = new Stub("Anthropic");
        var alt = new Stub("OpenAI");

        var client = LlmSupervisorDecider.BuildBrainClient(current, Pick("Anthropic"), new[] { (alt as IStructuredLLMClient, Pick("OpenAI")) });

        var failover = client.ShouldBeOfType<FailoverStructuredClient>();
        failover.Candidates.Count.ShouldBe(2);
        failover.Candidates[0].Client.ShouldBeSameAs(current, "the auto-selected brain still tries FIRST — failover changes what happens on a fault, never the pick");
        failover.Candidates[0].Pick.ModelId.ShouldBe("Anthropic-model");
        failover.Candidates[1].Client.ShouldBeSameAs(alt);
    }

    [Fact]
    public void The_per_decision_candidate_cap_is_pinned()
    {
        LlmSupervisorDecider.MaxBrainCandidates.ShouldBe(3, "current + two alternates — a decision turn resolves (decrypts) at most this many brain credentials");
    }

    private static ModelPoolPick Pick(string provider) => new() { ModelId = $"{provider}-model", Credential = new ResolvedModelCredential { Provider = provider, ApiKey = "k" } };

    private sealed class Stub : ILLMClient, IStructuredLLMClient
    {
        public Stub(string provider) => Provider = provider;
        public string Provider { get; }
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct) =>
            Task.FromResult(new StructuredLLMCompletion { Json = JsonSerializer.SerializeToElement(new { }), Model = request.Model });
    }
}
