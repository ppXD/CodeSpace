using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Agents;

/// <summary>
/// Q4 (SOTA-claim gate): the qualification claim board — every registered (mode × capability) pair's measured
/// performance standing, resolved from the immutable receipt ledger at read time. Sealed appears ONLY while a
/// current sealed receipt backs it (receipt id + suite digest + expiry on the row); a lapsed or revoked receipt
/// downgrades the board with no code change. Platform-level standing (receipts are not team rows); any team
/// member may read it — but a row's paying team and its model-credential ids reach only a reader in that team or a
/// global admin, and read null for everyone else.
/// </summary>
public sealed record GetQualificationClaimsQuery : IQuery<QualificationClaimBoard>, IRequireTeamMembership;
