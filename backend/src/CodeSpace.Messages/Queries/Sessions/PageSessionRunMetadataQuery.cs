using System.ComponentModel.DataAnnotations;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Sessions;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Sessions;

/// <summary>
/// One team-governed, hard-bounded page over immutable Session membership. Exactly one identity arm is populated by
/// the route. MembershipHeadRunNumber is carried inside the opaque Older cursor; mutable row fields are fresh reads.
/// </summary>
public sealed record PageSessionRunMetadataQuery : IQuery<SessionRunMetadataPage?>, IRequireTeamMembership, IValidatableObject
{
    public const int DefaultPageSize = 128;
    public const int MaximumPageSize = 256;
    public const int MaximumCursorLength = 512;

    public Guid? SessionId { get; init; }
    public Guid? RunAnchorId { get; init; }
    public SessionRunMetadataPageDirection Direction { get; init; } = SessionRunMetadataPageDirection.Tail;
    public string? Cursor { get; init; }
    public int Limit { get; init; } = DefaultPageSize;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if ((SessionId is { } sessionId && sessionId != Guid.Empty) == (RunAnchorId is { } runId && runId != Guid.Empty))
            yield return new ValidationResult("Exactly one non-empty SessionId or RunAnchorId is required.", [nameof(SessionId), nameof(RunAnchorId)]);
        if (!Enum.IsDefined(Direction))
            yield return new ValidationResult("Direction must be Tail or Older.", [nameof(Direction)]);
        if (Limit is < 1 or > MaximumPageSize)
            yield return new ValidationResult($"Limit must be between 1 and {MaximumPageSize}.", [nameof(Limit)]);
        if (Direction == SessionRunMetadataPageDirection.Tail && Cursor != null)
            yield return new ValidationResult("Tail does not accept a cursor.", [nameof(Cursor)]);
        if (Direction == SessionRunMetadataPageDirection.Older && (string.IsNullOrWhiteSpace(Cursor) || Cursor.Length > MaximumCursorLength))
            yield return new ValidationResult($"Older requires a non-blank cursor of at most {MaximumCursorLength} characters.", [nameof(Cursor)]);
    }
}
