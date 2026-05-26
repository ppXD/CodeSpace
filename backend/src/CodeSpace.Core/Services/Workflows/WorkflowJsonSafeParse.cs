using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows;

/// <summary>
/// Defensive deserialization of JSON-shaped columns. Centralised so every read path
/// (<see cref="WorkflowService"/>, future run-history filters, exporters) shares the same
/// fallback contract: null / whitespace / unparseable input degrades to an empty object
/// instead of throwing.
///
/// <para>Why this matters even though Postgres' jsonb columns reject invalid input at
/// write time: (1) future migrations may swap a column from <c>jsonb</c> to <c>text</c>
/// for re-encryption / debugging, (2) service code paths might one day persist via a
/// pathway other than EF and accidentally write an empty string, (3) tests + tooling
/// sometimes hand-construct DTOs without a backing row. One read-side helper protects
/// every consumer against all three.</para>
/// </summary>
internal static class WorkflowJsonSafeParse
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>
    /// Returns the element parsed from <paramref name="raw"/>, or <c>{}</c> on any of:
    /// null, empty, whitespace-only, or invalid JSON. The returned element is cloned so
    /// the caller may safely retain it past any <see cref="JsonDocument"/> disposal.
    /// </summary>
    public static JsonElement SafeParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return EmptyObject;

        try
        {
            return JsonDocument.Parse(raw).RootElement.Clone();
        }
        catch (JsonException)
        {
            return EmptyObject;
        }
    }
}
