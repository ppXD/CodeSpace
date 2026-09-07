using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>Shared transport schema and consumer-contract validation. It reports violations to the existing bounded model repair path without mutating the response.</summary>
internal static class StructuredResponseValidation
{
    public static IReadOnlyList<string> Validate(JsonElement response, StructuredLLMCompletionRequest request)
    {
        var errors = JsonSchemaValidator.Validate(response, request.JsonSchema);
        if (errors.Count > 0 || request.ResponseValidator is null) return errors;
        return request.ResponseValidator(response).Take(12).Select(error => error.Length > 512 ? error[..512] : error).ToArray();
    }
}
