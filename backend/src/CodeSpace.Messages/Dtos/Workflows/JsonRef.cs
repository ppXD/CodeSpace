namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// Explicit reference object as it appears in node Inputs JSON:
/// <c>{ "$ref": "nodes.fetch_diff.outputs.files" }</c>. The string is a dotted path the
/// <c>VariableResolver</c> walks against the engine's <c>NodeRunScope</c>.
///
/// Used when the value MUST be a structured object — passing a JSON object or array
/// directly. Inline <c>{{nodes.foo.outputs.bar}}</c> templates work for scalars (numbers,
/// strings, booleans) but interpolating an object into a string would stringify it. JsonRef
/// avoids that lossy round-trip.
///
/// Convention: an object with EXACTLY the single property "$ref" is interpreted as a
/// JsonRef. Anything else is treated as a literal object value.
/// </summary>
public sealed record JsonRef
{
    public const string PropertyName = "$ref";

    public required string Path { get; init; }
}
