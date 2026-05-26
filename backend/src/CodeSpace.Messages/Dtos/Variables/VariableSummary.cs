using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Variables;

/// <summary>
/// Operator-facing view of a variable. Carries the metadata + the value for non-secret
/// types. <see cref="VariableValueType.Secret"/> rows return <see cref="ValuePlain"/> as
/// null — by design, not by omission. The list / get endpoints NEVER decrypt + return
/// secret values; the only path that surfaces plaintext is
/// <c>IVariableService.GetAllForEngineAsync</c>, which is in-process and engine-only.
///
/// <para>For non-secret types <see cref="ValuePlain"/> is the JSON-encoded value as
/// stored in <c>variable.value_plain</c>. The UI parses it into a typed editor based on
/// <see cref="ValueType"/>.</para>
/// </summary>
public sealed record VariableSummary
{
    public required Guid Id { get; init; }
    public required VariableScope Scope { get; init; }
    public required Guid ScopeId { get; init; }
    public required Guid TeamId { get; init; }
    public required string Name { get; init; }
    public required VariableValueType ValueType { get; init; }
    /// <summary>JSON-encoded value when ValueType != Secret; always null for Secret.</summary>
    public string? ValuePlain { get; init; }
    public string? Description { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
    public required DateTimeOffset LastModifiedDate { get; init; }
}
