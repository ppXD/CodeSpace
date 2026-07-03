using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// The HONEST structured fake for the OUTPUT-REVIEW critic flows (triad S6) — the review sibling of
/// <see cref="DeterministicWorkPlanLlmClient"/>. The executor runs the production <c>LlmStructuredCritic</c>
/// (real pool resolve, real <c>CompleteStructuredAsync</c>, real Gate-schema projection); only the network call is
/// replaced. Its judgment is a PURE FUNCTION of the artifact under review: a prompt carrying
/// <see cref="RejectMarker"/> is DISAPPROVED with a deterministic critique naming the marker, anything else is
/// approved — so a revise round that actually removes the flaw flips the verdict, and one that doesn't can't.
/// The fixture-singleton <see cref="CriticReviewScript"/> counts calls (billing-order assertions) — tests reset it.
/// </summary>
public sealed class DeterministicCriticLlmClient : ILLMClient, IStructuredLLMClient
{
    /// <summary>Distinct provider tag — sits beside the other fakes + the real client with no duplicate-provider collision.</summary>
    public const string ProviderTag = "TestCritic";

    /// <summary>An artifact (diff) carrying this marker is DISAPPROVED — the planted flaw a revision must remove to pass.</summary>
    public const string RejectMarker = "TODO-HACK";

    /// <summary>The deterministic critique the disapproval carries — the revise loop feeds it back, so the E2Es assert it verbatim in the revise instruction.</summary>
    public const string Critique = "the change still carries a placeholder hack";

    private readonly CriticReviewScript _script;

    public DeterministicCriticLlmClient(CriticReviewScript script) { _script = script; }

    public string Provider => ProviderTag;

    public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new LLMCompletion { Text = "reviewed", Model = request.Model, Usage = new() { InputTokens = 2, OutputTokens = 2 } });

    public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
    {
        _script.Calls++;

        var flawed = request.UserPrompt.Contains(RejectMarker, StringComparison.Ordinal);

        // The critique deliberately does NOT repeat the marker: the revise loop echoes the critique into the next
        // round's goal (and the goal echoes into the next review prompt), so a marker-quoting critique would keep
        // re-triggering the reject forever — only the DIFF's own content may carry the marker.
        var json = flawed
            ? JsonSerializer.SerializeToElement(new { approved = false, score = 3, issues = new[] { "contains a placeholder hack" }, rationale = Critique })
            : JsonSerializer.SerializeToElement(new { approved = true, score = 9, issues = Array.Empty<string>(), rationale = "clean and goal-aligned" });

        return Task.FromResult(new StructuredLLMCompletion { Json = json, Model = request.Model, Usage = new() { InputTokens = 11, OutputTokens = 7 } });
    }
}

/// <summary>The fixture-singleton counter the critic fake bumps per structured call — how a test proves the ORDER of billing (e.g. a failed oracle never bills a review). Reset it at test start (the fixture is shared across the Postgres collection).</summary>
public sealed class CriticReviewScript
{
    public int Calls { get; set; }

    public void Reset() => Calls = 0;
}
