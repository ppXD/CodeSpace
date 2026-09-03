using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.ModelCredentials;

/// <summary>
/// Manually add a model to a credential's maintained list (<c>Source = Manual</c>) — the "type a model id" half
/// of the pick-or-type surface, so a custom / gateway model id can be entered once and then picked thereafter.
/// <see cref="ModelCredentialId"/> is the route's authoritative credential id (merged in by the controller,
/// Rule 17). Returns the new row id.
/// </summary>
public sealed record AddCredentialedModelCommand : ICommand<Guid>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.ModelsManage;

    public Guid ModelCredentialId { get; init; }
    public required string ModelId { get; init; }
    public string? DisplayName { get; init; }

    /// <summary>D1 — optional USD per 1,000,000 input tokens. Carried on ADD (not just on the price endpoint) because the editor reconciles a renamed row as remove-then-add: without it, editing a model's display name would silently drop the price the operator typed and re-break the run's cost cap.</summary>
    public decimal? InputUsdPerMillion { get; init; }

    /// <summary>D1 — optional USD per 1,000,000 output tokens (see <see cref="InputUsdPerMillion"/>).</summary>
    public decimal? OutputUsdPerMillion { get; init; }
}
