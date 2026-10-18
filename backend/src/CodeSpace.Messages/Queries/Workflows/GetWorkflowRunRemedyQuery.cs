using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Workflows;

/// <summary>
/// What is wrong with the provider connection a run failed on, and what the asking user can do about it — the lean read the run
/// page makes for a failed turn, so it does not pull a whole run detail (every node's inputs, outputs and the definition) to
/// learn which button to show. Null when the run recorded no failure a person can repair. Team membership alone, like the run
/// detail it mirrors: the answer names the credential and the people who can fix it, which the team's Providers list already
/// shows every member.
/// </summary>
public sealed record GetWorkflowRunRemedyQuery : IQuery<ProviderRemedy?>, IRequireTeamMembership
{
    public required Guid RunId { get; init; }
}
