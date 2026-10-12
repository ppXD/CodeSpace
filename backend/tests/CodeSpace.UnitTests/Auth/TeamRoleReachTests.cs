using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Auth;

/// <summary>
/// 🟢 Unit: rank is a reach — strictly below, or at your own rank only when the subject is your own (yourself, or an
/// invitation you sent). An invitation is the role it grants, waiting to be accepted, so the Admin who could regenerate
/// an Owner invitation could make themselves Owner. Revoking one raises nobody, though, so it reaches every invitation
/// up to your own rank — or a leaked Owner link would have nobody able to kill it once its sender left.
/// </summary>
[Trait("Category", "Unit")]
public class TeamRoleReachTests
{
    [Theory]
    [InlineData(TeamRole.Owner, TeamRole.Admin, false, true)]
    [InlineData(TeamRole.Owner, TeamRole.Viewer, false, true)]
    [InlineData(TeamRole.Admin, TeamRole.Member, false, true)]
    [InlineData(TeamRole.Member, TeamRole.Viewer, false, true)]
    [InlineData(TeamRole.Admin, TeamRole.Admin, true, true)]     // your own, at your own rank
    [InlineData(TeamRole.Owner, TeamRole.Owner, true, true)]
    [InlineData(TeamRole.Admin, TeamRole.Admin, false, false)]   // across
    [InlineData(TeamRole.Owner, TeamRole.Owner, false, false)]   // across, at the top
    [InlineData(TeamRole.Admin, TeamRole.Owner, false, false)]   // above
    [InlineData(TeamRole.Admin, TeamRole.Owner, true, false)]    // above, even if it is yours
    [InlineData(TeamRole.Viewer, TeamRole.Member, false, false)]
    public void Reaches_only_below_or_your_own_at_your_rank(TeamRole actor, TeamRole subject, bool isOwn, bool expected)
    {
        TeamRoleRank.Reaches(actor, subject, isOwn).ShouldBe(expected);
    }

    [Theory]
    [InlineData(TeamRole.Owner, TeamRole.Owner, false, true)]    // any Owner invitation — a co-Owner's, a departed Owner's
    [InlineData(TeamRole.Admin, TeamRole.Admin, false, true)]    // a peer's: withdrawing a grant raises nobody
    [InlineData(TeamRole.Admin, TeamRole.Viewer, false, true)]
    [InlineData(TeamRole.Admin, TeamRole.Owner, false, false)]   // above, and someone else's
    [InlineData(TeamRole.Member, TeamRole.Admin, false, false)]
    [InlineData(TeamRole.Admin, TeamRole.Owner, true, true)]     // your own, sent as Owner before a demotion — still yours to withdraw
    public void Revoking_an_invitation_reaches_up_to_your_own_rank_or_one_you_sent(TeamRole actor, TeamRole invitationRole, bool isOwn, bool expected)
    {
        TeamRoleRank.MayRevokeInvitation(actor, invitationRole, isOwn).ShouldBe(expected);
    }

    [Theory]
    [InlineData(TeamRole.Owner, TeamRole.Owner, false, true)]   // nobody ranks above an Owner, who may invite at Owner outright
    [InlineData(TeamRole.Owner, TeamRole.Admin, false, true)]
    [InlineData(TeamRole.Admin, TeamRole.Admin, true, true)]    // your own, at your own rank
    [InlineData(TeamRole.Admin, TeamRole.Member, false, true)]
    [InlineData(TeamRole.Admin, TeamRole.Admin, false, false)]  // across: it hands the caller a working link to a peer's grant
    [InlineData(TeamRole.Admin, TeamRole.Owner, false, false)]  // the escalation
    [InlineData(TeamRole.Admin, TeamRole.Owner, true, false)]   // your own, sent as Owner before a demotion
    public void Regenerating_an_invitation_follows_the_reach_rule_except_for_an_owner(TeamRole actor, TeamRole invitationRole, bool isOwn, bool expected)
    {
        TeamRoleRank.MayRegenerateInvitation(actor, invitationRole, isOwn).ShouldBe(expected);
    }
}
