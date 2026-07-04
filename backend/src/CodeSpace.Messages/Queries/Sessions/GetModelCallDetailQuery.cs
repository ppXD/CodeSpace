using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Sessions;

/// <summary>
/// The full, on-demand detail of ONE model call in a run — the prompt · result · usage · trace the journal's model-call
/// drawer shows, keyed by the completed interaction record's <see cref="Sequence"/> (the same sequence the model-call
/// step's <c>record-{Sequence}</c> id carries). Team-scoped (<see cref="IRequireTeamMembership"/>); a foreign / missing
/// run or an unknown sequence is an indistinguishable not-found (null → 404).
/// </summary>
public sealed record GetModelCallDetailQuery : IQuery<ModelCallDetail?>, IRequireTeamMembership
{
    public Guid RunId { get; init; }

    /// <summary>The completed interaction record's ledger sequence — the model-call step's id is <c>record-{Sequence}</c>.</summary>
    public long Sequence { get; init; }
}
