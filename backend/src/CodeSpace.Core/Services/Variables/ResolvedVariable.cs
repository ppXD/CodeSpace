using System.Text.Json;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Variables;

/// <summary>
/// Engine-only DTO. Carries a single variable's name + value + value-type as the engine
/// sees it at scope-build time. <see cref="Value"/> for Secret-typed entries is the
/// decrypted plaintext JSON-encoded (as <c>{"":"..."}</c>'s value member, i.e. the raw
/// string wrapped in a JsonElement for uniform consumption by VariableResolver).
///
/// <para>Differs from <c>VariableSummary</c> in two ways:
/// (1) carries the actual value for Secret entries (necessary for the engine to bind
///     scope.Wf / scope.Team correctly); (2) is in CodeSpace.Core (not Messages) because
///     it MUST NEVER cross the API boundary — only the in-process engine consumes it.</para>
///
/// <para>The engine downgrades this to <c>JsonElement</c> when populating the scope
/// dictionaries (matches the existing NodeRunScope shape).</para>
/// </summary>
public sealed record ResolvedVariable
{
    public required string Name { get; init; }
    public required VariableValueType ValueType { get; init; }
    public required JsonElement Value { get; init; }
}
