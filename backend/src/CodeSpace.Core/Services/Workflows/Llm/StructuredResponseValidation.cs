using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>Shared transport schema and consumer-contract validation. It reports violations to the existing bounded model repair path without mutating the response.</summary>
internal static class StructuredResponseValidation
{
    public static IReadOnlyList<string> Validate(JsonElement response, StructuredLLMCompletionRequest request)
    {
        var errors = JsonSchemaValidator.Validate(response, request.JsonSchema);
        if (errors.Count > 0 || request.ResponseValidator is null) return errors;
        return Bound(request.ResponseValidator(response));
    }

    /// <summary>The re-ask-only half of the consumer contract (<see cref="StructuredLLMCompletionRequest.ResponseAdvisor"/>). Asked only of a response that is otherwise valid, so an already-failing reply is corrected on its fatal faults alone.</summary>
    public static IReadOnlyList<string> Advise(JsonElement response, StructuredLLMCompletionRequest request) =>
        request.ResponseAdvisor is null ? Array.Empty<string>() : Bound(request.ResponseAdvisor(response));

    private static IReadOnlyList<string> Bound(IReadOnlyList<string> reported) =>
        reported.Take(12).Select(error => error.Length > 512 ? error[..512] : error).ToArray();
}
