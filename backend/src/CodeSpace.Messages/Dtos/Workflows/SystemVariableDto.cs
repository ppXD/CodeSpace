namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// API-exposed view of a single <c>sys.*</c> variable — feeds the editor's read-only
/// system tab and the {{}} autocomplete picker. The engine's
/// <c>SystemScopeKeys.Descriptors</c> is the canonical source; this DTO is the wire shape.
/// </summary>
public sealed record SystemVariableDto
{
    /// <summary>Key under <c>sys.</c> — e.g. "workflow_run_id".</summary>
    public required string Key { get; init; }

    /// <summary>JSON schema-style type hint ("string", "integer", …). Display-only.</summary>
    public required string Type { get; init; }

    /// <summary>One-line operator-facing description, shown next to the key in the editor.</summary>
    public required string Description { get; init; }
}
