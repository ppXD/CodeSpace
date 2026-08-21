using System.ComponentModel.DataAnnotations;
using System.Globalization;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Agents;

/// <summary>
/// One hard-bounded keyset page over an Agent Run's append-only event log. The three directions are
/// deliberately mutually exclusive: <see cref="AgentRunEventPageDirection.Tail"/> accepts no cursor,
/// while Older/Newer require one exact decimal sequence. The legacy whole-log query remains available.
/// </summary>
public sealed record PageAgentRunEventsQuery : IQuery<AgentRunEventPage?>, IRequireTeamMembership, IValidatableObject
{
    public const int DefaultPageSize = 200;
    public const int MaximumPageSize = 500;

    public required Guid AgentRunId { get; init; }
    public AgentRunEventPageDirection Direction { get; init; } = AgentRunEventPageDirection.Tail;

    /// <summary>An invariant, unsigned decimal event sequence. Opaque text at the HTTP boundary makes signs, whitespace, and overflow rejectable before any database read.</summary>
    public string? Cursor { get; init; }

    public int Limit { get; init; } = DefaultPageSize;

    public bool TryGetCursor(out long cursor) =>
        long.TryParse(Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out cursor)
        && cursor >= 0
        && (Direction != AgentRunEventPageDirection.Older || cursor > 0);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enum.IsDefined(Direction))
        {
            yield return new ValidationResult("Direction must be Tail, Older, or Newer.", [nameof(Direction)]);
            yield break;
        }

        if (Limit is < 1 or > MaximumPageSize)
            yield return new ValidationResult($"Limit must be between 1 and {MaximumPageSize}.", [nameof(Limit)]);

        if (Direction == AgentRunEventPageDirection.Tail)
        {
            if (Cursor != null) yield return new ValidationResult("Tail does not accept a cursor.", [nameof(Cursor)]);
            yield break;
        }

        if (!TryGetCursor(out _))
            yield return new ValidationResult(Direction == AgentRunEventPageDirection.Older
                ? "Older requires a positive invariant decimal cursor."
                : "Newer requires a non-negative invariant decimal cursor.", [nameof(Cursor)]);
    }
}

public enum AgentRunEventPageDirection
{
    Tail,
    Older,
    Newer,
}
