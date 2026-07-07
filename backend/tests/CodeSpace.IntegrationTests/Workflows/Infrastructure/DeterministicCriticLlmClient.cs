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

    /// <summary>An artifact (diff) carrying this marker is DISAPPROVED with a BLOCKER — the planted flaw a revision must remove to pass.</summary>
    public const string RejectMarker = "TODO-HACK";

    /// <summary>An artifact carrying this marker (but NOT <see cref="RejectMarker"/>) draws a MINOR-only flag — the model raises a nitpick but the severity-authoritative projection APPROVES it, so the calibration path (no halt, no revise) is exercised deterministically.</summary>
    public const string NitpickMarker = "STYLE-NIT";

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
        var nitpick = !flawed && request.UserPrompt.Contains(NitpickMarker, StringComparison.Ordinal);

        // The critique (and its evidence) deliberately does NOT repeat the marker: the revise loop echoes the critique
        // into the next round's goal (and the goal echoes into the next review prompt), so a marker-quoting critique
        // would keep re-triggering the reject forever — only the DIFF's own content may carry the marker. Issues carry
        // the S8 evidence-attached + P1 severity-graded shape ({issue, evidence, severity}) the Gate schema now
        // requires; the planted hack is a BLOCKER (severity-authoritative → the projected Approved stays false), so the
        // revise loop fires exactly as before. A NITPICK draws a MINOR issue with the model's raw approved:false — the
        // severity-authoritative projection APPROVES it anyway (no blocker), exercising the calibration path.
        var json = flawed
            ? JsonSerializer.SerializeToElement(new { approved = false, score = 3, issues = new[] { new { issue = "contains a placeholder hack", evidence = "the flaw marker is present in the diff", severity = "blocker" } }, rationale = Critique })
            : nitpick
                ? JsonSerializer.SerializeToElement(new { approved = false, score = 8, issues = new[] { new { issue = "a terse local name could be clearer", evidence = "the naming in the diff", severity = "minor" } }, rationale = "sound; one cosmetic nit" })
                : JsonSerializer.SerializeToElement(new { approved = true, score = 9, issues = Array.Empty<object>(), rationale = "clean and goal-aligned" });

        return Task.FromResult(new StructuredLLMCompletion { Json = json, Model = request.Model, Usage = new() { InputTokens = 11, OutputTokens = 7 } });
    }
}

/// <summary>The fixture-singleton counter the critic fake bumps per structured call — how a test proves the ORDER of billing (e.g. a failed oracle never bills a review). Reset it at test start (the fixture is shared across the Postgres collection).</summary>
public sealed class CriticReviewScript
{
    public int Calls { get; set; }

    public void Reset() => Calls = 0;
}
