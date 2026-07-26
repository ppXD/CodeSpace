using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Messages.Commands.Tasks;

/// <summary>
/// P5-7 (I1 spec compiler, first slice): compile a free-text goal into TYPED launch-contract SUGGESTIONS —
/// an executable acceptance argv, definition-of-done criteria, a delivery preference — that the launch composer
/// PRE-FILLS as editable fields. Nothing is persisted and nothing is staked: whatever the operator keeps arrives
/// on the ordinary <c>LaunchTaskCommand</c> fields and inherits Operator authority BY CONSTRUCTION (the P5-4
/// provenance carrier) — unconfirmed model output never touches the ledger at all.
///
/// <para>Tenancy: <see cref="IRequireTeamMembership"/>; the team is resolved from <c>ICurrentTeam</c>, never this
/// body. <see cref="RepositoryId"/> grounds the suggestions in the repo's top-level layout, resolved TEAM-SCOPED —
/// a repo outside the team yields no grounding (fail-closed, never a cross-team read).</para>
/// </summary>
public sealed record CompileTaskSpecCommand : ICommand<CompileTaskSpecResult>, IRequireTeamMembership
{
    /// <summary>The operator's free-text goal to compile.</summary>
    public required string Goal { get; init; }

    /// <summary>Optional repository the task concerns — grounds the suggested check in the repo's actual toolchain.</summary>
    public Guid? RepositoryId { get; init; }
}
