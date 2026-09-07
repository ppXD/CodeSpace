using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>Shared transport schema and consumer-contract validation, plus the ONE definition of what a violation costs: which severity earns the bounded re-ask, which preamble that re-ask carries, and which outcome is a fault. It reports violations to the model repair path without ever mutating the response.</summary>
internal static class StructuredResponseValidation
{
    public static IReadOnlyList<string> Validate(JsonElement response, StructuredLLMCompletionRequest request)
    {
        var errors = JsonSchemaValidator.Validate(response, request.JsonSchema);
        if (errors.Count > 0 || request.ResponseValidator is null) return errors;
        return Bound(request.ResponseValidator(response));
    }

    /// <summary>
    /// The validate → bounded-re-ask → decide sequence for one structured completion. It lives here, not in each
    /// client, because the two providers must not drift on it: the fix below was applied to one client and not the
    /// other for exactly as long as there were two copies.
    ///
    /// <para>A first reply with real validator errors keeps the original contract exactly — one re-ask, then a typed
    /// <see cref="LlmErrorCategory.Malformed"/> fault. A first reply that is VALID and only ADVISORY is already an
    /// answer its consumer degrades, so the re-ask can only improve on it: a second reply that fails validation, or a
    /// second call that yields no JSON at all, returns the FIRST reply rather than trading a degradable answer for a
    /// fault. That trade was not hypothetical — advice naming a payload to author can steer a model into authoring it
    /// on the wrong oracle, which is fatal by design, so the re-ask itself could kill the plan it was sent to save.</para>
    /// </summary>
    public static async Task<StructuredLLMCompletion> ReaskOnceThenDecideAsync(StructuredLLMCompletion first, StructuredLLMCompletionRequest request, string provider, Func<string, Task<StructuredLLMCompletion>> reaskAsync)
    {
        var errors = Validate(first.Json, request);
        var advisories = errors.Count == 0 ? Advise(first.Json, request) : Array.Empty<string>();

        if (errors.Count == 0 && advisories.Count == 0) return first;

        var degradable = errors.Count == 0;   // `first` is usable as-is ⇒ the re-ask is an upgrade attempt, never a gate

        var feedback = degradable
            ? StructuredJsonText.WithAdvisoryFeedback(request.SystemPrompt, advisories, first.Json)
            : StructuredJsonText.WithValidationFeedback(request.SystemPrompt, errors, first.Json);

        StructuredLLMCompletion second;

        try
        {
            second = await reaskAsync(feedback).ConfigureAwait(false);
        }
        catch (LlmApiException ex) when (degradable && ex.Category == LlmErrorCategory.Malformed)
        {
            // The upgrade attempt produced no parseable JSON. Its usage died with the exception, so what remains is a
            // subtotal — but the ANSWER is still the first reply, and it is not this call's job to fail.
            return first with { Usage = first.Usage with { IsPartial = true } };
        }

        var errors2 = Validate(second.Json, request);

        if (errors2.Count == 0) return Billed(second, first);

        if (degradable) return Billed(first, second);   // the re-ask made a valid reply WORSE; keep the one that still answers

        throw new LlmApiException(provider, null, LlmErrorCategory.Malformed,
            $"structured output failed schema validation after a re-ask: {string.Join("; ", errors2)}");
    }

    /// <summary>The re-ask-only half of the consumer contract (<see cref="StructuredLLMCompletionRequest.ResponseAdvisor"/>), asked ONLY of an otherwise-valid response — a precondition the single caller above enforces, so an already-failing reply is corrected on its fatal faults alone.</summary>
    private static IReadOnlyList<string> Advise(JsonElement response, StructuredLLMCompletionRequest request) =>
        request.ResponseAdvisor is null ? Array.Empty<string>() : Bound(request.ResponseAdvisor(response));

    /// <summary>Both physical calls were billed, so the returned envelope totals them — keeping the first reply as the answer must never under-report the cost of having asked twice. The finish reason stays the ACCEPTED reply's own: <see cref="LlmUsage.Add"/> takes the later call's, which is the REJECTED one whenever the answer is the first.</summary>
    private static StructuredLLMCompletion Billed(StructuredLLMCompletion accepted, StructuredLLMCompletion rejected) => accepted with
    {
        Usage = accepted.Usage.Add(rejected.Usage, string.Equals(accepted.Model, rejected.Model, StringComparison.OrdinalIgnoreCase)) with { FinishReason = accepted.Usage.FinishReason },
    };

    private static IReadOnlyList<string> Bound(IReadOnlyList<string> reported) =>
        reported.Take(12).Select(error => error.Length > 512 ? error[..512] : error).ToArray();
}
