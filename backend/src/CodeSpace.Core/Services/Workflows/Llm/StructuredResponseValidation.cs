using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// One consumer-contract defect the model gets a bounded re-ask to fix and which does NOT fail the call when the
/// re-ask does not fix it (a data noun — <see cref="StructuredResponseValidation"/> decides what it costs).
/// </summary>
public sealed record StructuredResponseAdvisory
{
    /// <summary>The advice the re-ask carries: what to author, and the honest alternative to inventing it.</summary>
    public required string Message { get; init; }

    /// <summary>
    /// The instance path this defect explains, in <c>JsonSchemaValidator</c>'s notation (e.g.
    /// <c>$.subtasks[0].acceptance</c>). A SCHEMA violation at or under it is this SAME defect, said by the schema
    /// instead of by the consumer, so it inherits this severity rather than failing the call.
    ///
    /// <para>That attribution is what keeps a degrade working once the schema learns to express the same requirement:
    /// a per-kind <c>oneOf</c> that requires the payload turns every payload-less reply into a schema error, and
    /// without a claimed path each one is silently promoted back into the fault the degrade exists to prevent.</para>
    ///
    /// <para>The root (<c>$</c>) claims nothing — an advisory that owned it would silence the whole schema check.</para>
    /// </summary>
    public required string Path { get; init; }
}

/// <summary>Shared transport schema and consumer-contract validation, plus the ONE definition of what a violation costs: which severity earns the bounded re-ask, which preamble that re-ask carries, and which outcome is a fault. It reports violations to the model repair path without ever mutating the response.</summary>
internal static class StructuredResponseValidation
{
    private const int MaxReported = 12;   // a long list helps nobody; cap so the re-ask prompt stays focused

    /// <summary>
    /// The validate → bounded-re-ask → decide sequence for one structured completion. It lives here, not in each
    /// client, because the two providers must not drift on it: the fix below was applied to one client and not the
    /// other for exactly as long as there were two copies.
    ///
    /// <para>A first reply with FATAL defects keeps the original contract exactly — one re-ask, then a typed
    /// <see cref="LlmErrorCategory.Malformed"/> fault. A first reply that is only ADVISORY is already an answer its
    /// consumer degrades, so the re-ask can only improve on it: a second reply that is anything less than clean, or a
    /// second call that yields no JSON at all, returns the FIRST reply rather than trading a degradable answer for a
    /// worse one. Advice naming what to author can steer a model into authoring it wrongly somewhere ELSE — a payload
    /// on the wrong oracle — so an upgrade attempt that is not clean must never be allowed to replace the answer.</para>
    ///
    /// <para>The mirror of that rule applies to a FATAL first reply: a second one whose remaining defects are all
    /// degradable IS the answer, because an advisory defect is not a fault no matter which attempt carries it.</para>
    ///
    /// <para>That rule is about the transport too, not only about what the second reply says. Once the first reply is
    /// degradable we HOLD a usable answer, so ANY <see cref="LlmApiException"/> the upgrade attempt raises — a 429, a
    /// 5xx, a lost connection, no parseable JSON — returns the first reply with an honestly partial usage instead of
    /// propagating. Propagating it destroyed an answer the consumer had already accepted and bought a park for the
    /// chance of a fresh attempt; a run parked at planning is worth strictly less than the plan we were holding.
    /// A FATAL first reply is not affected: there is no answer to keep, so its re-ask still fails the call.</para>
    /// </summary>
    public static async Task<StructuredLLMCompletion> ReaskOnceThenDecideAsync(StructuredLLMCompletion first, StructuredLLMCompletionRequest request, string provider, Func<string, Task<StructuredLLMCompletion>> reaskAsync)
    {
        var defects = Classify(first.Json, request);

        if (defects.IsClean) return first;

        var degradable = defects.Fatal.Count == 0;   // `first` is usable as-is ⇒ the re-ask is an upgrade attempt, never a gate

        var feedback = degradable
            ? StructuredJsonText.WithAdvisoryFeedback(request.SystemPrompt, defects.Advisories.Select(advisory => advisory.Message).ToArray(), first.Json)
            : StructuredJsonText.WithValidationFeedback(request.SystemPrompt, defects.Fatal, first.Json);

        StructuredLLMCompletion second;

        try
        {
            second = await reaskAsync(feedback).ConfigureAwait(false);
        }
        catch (LlmApiException) when (degradable)
        {
            // The upgrade attempt failed — on its content (no parseable JSON) or on the transport (rate limit,
            // gateway fault, lost connection). Either way its usage died with the exception, so what remains is a
            // subtotal; but the ANSWER is still the first reply, and it is not this call's job to fail.
            return first with { Usage = first.Usage with { IsPartial = true } };
        }

        var repaired = Classify(second.Json, request);

        if (degradable) return repaired.IsClean ? Billed(second, first) : Billed(first, second);   // only a CLEAN reply replaces an answer; anything else kept the defect or added one

        if (repaired.Fatal.Count == 0) return Billed(second, first);

        throw new LlmApiException(provider, null, LlmErrorCategory.Malformed,
            $"structured output failed schema validation after a re-ask: {string.Join("; ", repaired.Fatal)}");
    }

    /// <summary>
    /// Split ONE reply's defects into the two severities from a single definition of each. The
    /// <see cref="StructuredLLMCompletionRequest.ResponseAdvisor"/>'s findings are the degradable ones, and a SCHEMA
    /// violation at or under a path one of them CLAIMS is that same defect — the schema saying it too, not a second,
    /// fatal problem. Attribution is by path and by the advisor's own verdict together: a violation the consumer
    /// claimed nothing about stays fatal, however deep it sits, so what degrades is only ever what some consumer has
    /// said it can go on without.
    ///
    /// <para>The typed consumer check runs only once nothing fatal is left in the schema pass, exactly as before, so
    /// a reply is corrected on its worst defect rather than lectured about every one at once.</para>
    /// </summary>
    private static ResponseDefects Classify(JsonElement response, StructuredLLMCompletionRequest request)
    {
        IReadOnlyList<StructuredResponseAdvisory> advisories = request.ResponseAdvisor is null ? [] : Bound(request.ResponseAdvisor(response));

        var unexplained = JsonSchemaValidator.Validate(response, request.JsonSchema).Where(violation => !advisories.Any(advisory => Explains(advisory, violation)));
        var fatal = Bound(unexplained.ToArray());

        if (fatal.Count == 0 && request.ResponseValidator is not null) fatal = Bound(request.ResponseValidator(response));

        return new ResponseDefects(fatal, advisories);
    }

    /// <summary>Whether a schema violation is the advisory's OWN defect: its instance path is the claimed one or nested inside it. A claim on the root is refused rather than honoured — it would silence every schema error in the reply, which is the one way this seam could disable the check it is meant to interpret.</summary>
    private static bool Explains(StructuredResponseAdvisory advisory, string violation)
    {
        if (string.IsNullOrEmpty(advisory.Path) || advisory.Path == "$") return false;

        var at = JsonSchemaValidator.PathOf(violation);

        return at == advisory.Path || at.StartsWith(advisory.Path + ".", StringComparison.Ordinal) || at.StartsWith(advisory.Path + "[", StringComparison.Ordinal);
    }

    /// <summary>What one reply's defects COST: the violations that fail the call, and the ones the consumer degrades when a bounded re-ask does not fix them.</summary>
    private readonly record struct ResponseDefects(IReadOnlyList<string> Fatal, IReadOnlyList<StructuredResponseAdvisory> Advisories)
    {
        public bool IsClean => Fatal.Count == 0 && Advisories.Count == 0;
    }

    /// <summary>Both physical calls were billed, so the returned envelope totals them — keeping the first reply as the answer must never under-report the cost of having asked twice. The finish reason stays the ACCEPTED reply's own: <see cref="LlmUsage.Add"/> takes the later call's, which is the REJECTED one whenever the answer is the first.</summary>
    private static StructuredLLMCompletion Billed(StructuredLLMCompletion accepted, StructuredLLMCompletion rejected) => accepted with
    {
        Usage = accepted.Usage.Add(rejected.Usage, string.Equals(accepted.Model, rejected.Model, StringComparison.OrdinalIgnoreCase)) with { FinishReason = accepted.Usage.FinishReason },
    };

    private static IReadOnlyList<string> Bound(IReadOnlyList<string> reported) => reported.Take(MaxReported).Select(Shorten).ToArray();

    /// <summary>The advisory cap is a bound on the MODEL's contribution too — the message names entities the reply authored (a subtask id), so an absurd one must not be able to bloat the re-ask prompt.</summary>
    private static IReadOnlyList<StructuredResponseAdvisory> Bound(IReadOnlyList<StructuredResponseAdvisory> reported) =>
        reported.Take(MaxReported).Select(advisory => advisory with { Message = Shorten(advisory.Message) }).ToArray();

    private static string Shorten(string reported) => reported.Length > 512 ? reported[..512] : reported;
}
