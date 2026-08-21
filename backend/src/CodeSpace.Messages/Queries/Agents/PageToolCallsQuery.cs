using System.ComponentModel.DataAnnotations;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Agents;

public enum ToolCallPageDirection { Tail, Older }

/// <summary>A hard-bounded Tail or Older metadata page over one Agent Run's governed tool-call ledger.</summary>
public sealed record PageToolCallsQuery : IQuery<ToolCallPage?>, IRequireTeamMembership, IValidatableObject
{
    public const int DefaultPageSize = 128;
    public const int MaximumPageSize = 500;
    public const int MaximumCursorLength = 256;

    public required Guid AgentRunId { get; init; }
    public ToolCallPageDirection Direction { get; init; } = ToolCallPageDirection.Tail;
    public string? Cursor { get; init; }
    public int Limit { get; init; } = DefaultPageSize;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enum.IsDefined(Direction)) yield return new ValidationResult("Direction must be Tail or Older.", [nameof(Direction)]);
        if (Limit is < 1 or > MaximumPageSize) yield return new ValidationResult($"Limit must be between 1 and {MaximumPageSize}.", [nameof(Limit)]);

        if (Direction == ToolCallPageDirection.Tail && Cursor != null)
            yield return new ValidationResult("Tail does not accept a cursor.", [nameof(Cursor)]);
        if (Direction == ToolCallPageDirection.Older && (string.IsNullOrWhiteSpace(Cursor) || Cursor.Length > MaximumCursorLength))
            yield return new ValidationResult($"Older requires an opaque cursor of at most {MaximumCursorLength} characters.", [nameof(Cursor)]);
    }
}
