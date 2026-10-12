using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.Completion;

public interface IQualificationClaimBoardReader
{
    /// <summary>The claim board as the current caller may read it: every standing, with each paying team's identifiers kept to that team.</summary>
    Task<QualificationClaimBoard> ReadAsync(DateTimeOffset asOf, CancellationToken cancellationToken);
}

/// <summary>
/// The claim board's reader projection.
///
/// <para>The standings are platform-wide and every member of every team may read them. The receipt behind a row is
/// not: a round runs under the X-Team-Id of whichever team paid for it, so its cohort names that team and its verifier
/// bundle names that team's model credential and credentialed-model row. Those reach a reader in the paying team, and
/// a global admin, who minted the round; everyone else reads the same standing, harness, tier and receipt id with
/// those identifiers null.</para>
///
/// <para>The resolver stays a pure fold over the ledger; who is asking is this class's question.</para>
/// </summary>
public sealed class QualificationClaimBoardReader : IQualificationClaimBoardReader, IScopedDependency
{
    private readonly IQualificationClaimResolver _claims;
    private readonly ICurrentUser _currentUser;
    private readonly ICurrentTeam _currentTeam;

    public QualificationClaimBoardReader(IQualificationClaimResolver claims, ICurrentUser currentUser, ICurrentTeam currentTeam)
    {
        _claims = claims;
        _currentUser = currentUser;
        _currentTeam = currentTeam;
    }

    public async Task<QualificationClaimBoard> ReadAsync(DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        var board = await _claims.ResolveBoardAsync(asOf, cancellationToken).ConfigureAwait(false);

        return board with { Rows = board.Rows.Select(ForCaller).ToList() };
    }

    private PerformanceClaim ForCaller(PerformanceClaim claim) => MayReadPayingTeam(claim) ? claim : WithoutPayingTeam(claim);

    /// <summary>A global admin, or a caller whose verified team IS the paying team. A row with no typed cohort names no team, so nobody but an admin is entitled to its credential ids either.</summary>
    private bool MayReadPayingTeam(PerformanceClaim claim) => _currentUser.HasRole(Roles.Admin) || (claim.Cohort?.TeamId is { } payingTeam && payingTeam == _currentTeam.Id);

    internal static PerformanceClaim WithoutPayingTeam(PerformanceClaim claim) => claim with { Cohort = WithoutPayingTeam(claim.Cohort), Seal = WithoutPayingTeam(claim.Seal) };

    private static ClaimCohort? WithoutPayingTeam(ClaimCohort? cohort) => cohort is null ? null : cohort with { TeamId = null };

    private static ContractSeal? WithoutPayingTeam(ContractSeal? seal) => seal?.VerifierBundle is not { } bundle ? seal : seal with { VerifierBundle = bundle with { ModelCredentialId = null, ModelCredentialModelId = null } };
}
