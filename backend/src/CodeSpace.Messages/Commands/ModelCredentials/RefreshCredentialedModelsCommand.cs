using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.ModelCredentials;

/// <summary>
/// Reflect a credential's provider endpoint and refresh its model list (the "auto-suggest" half of the pick-or-type
/// surface) — a distinct, explicitly-triggered action so reflection's HTTP round-trip NEVER blocks credential save.
/// <see cref="ModelCredentialId"/> comes from the route (Rule 17). Returns the count of models reflected (0 for a
/// non-reflectable, manual-only credential).
/// </summary>
public sealed record RefreshCredentialedModelsCommand : ICommand<int>, IRequireTeamMembership
{
    public Guid ModelCredentialId { get; init; }
}
