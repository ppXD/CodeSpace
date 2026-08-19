using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Agents;

/// <summary>
/// Q4 (SOTA-claim gate): the qualification claim board — every registered (mode × capability) pair's measured
/// performance standing, resolved from the immutable receipt ledger at read time. Sealed appears ONLY while a
/// current sealed receipt backs it (receipt id + suite digest + expiry on the row); a lapsed or revoked receipt
/// downgrades the board with no code change. Platform-level standing (receipts are not team rows); any team
/// member may read it.
/// </summary>
public sealed record GetQualificationClaimsQuery : IQuery<QualificationClaimBoard>, IRequireTeamMembership;
