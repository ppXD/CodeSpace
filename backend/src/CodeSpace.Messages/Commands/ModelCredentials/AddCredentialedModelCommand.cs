using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.ModelCredentials;

/// <summary>
/// Manually add a model to a credential's maintained list (<c>Source = Manual</c>) — the "type a model id" half
/// of the pick-or-type surface, so a custom / gateway model id can be entered once and then picked thereafter.
/// <see cref="ModelCredentialId"/> is the route's authoritative credential id (merged in by the controller,
/// Rule 17). Returns the new row id.
/// </summary>
public sealed record AddCredentialedModelCommand : ICommand<Guid>, IRequireTeamMembership
{
    public Guid ModelCredentialId { get; init; }
    public required string ModelId { get; init; }
    public string? DisplayName { get; init; }
}
