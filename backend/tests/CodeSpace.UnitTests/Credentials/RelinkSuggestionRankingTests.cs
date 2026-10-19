using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Credentials;

/// <summary>
/// Which credentials are offered to take a dead one's bindings, and in what order: the caller's own live connections first
/// (newest first — the one just made is the one most likely replacing the dead one), then the team's service tokens
/// (newest first). A teammate's personal token is never offered, nor anything the relink itself would refuse.
/// </summary>
[Trait("Category", "Unit")]
public class RelinkSuggestionRankingTests
{
    private static readonly Guid Team = Guid.NewGuid();
    private static readonly Guid Instance = Guid.NewGuid();
    private static readonly Guid Caller = Guid.NewGuid();
    private static readonly Guid Teammate = Guid.NewGuid();
    private static readonly DateTimeOffset Revoked = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Own_connections_come_first_newest_first_then_team_tokens_newest_first()
    {
        var dead = Dead();
        var ownOld = Own(Revoked.AddDays(-3));
        var ownNew = Own(Revoked.AddDays(2));
        var teamOld = Team_(Revoked.AddDays(-9));
        var teamNew = Team_(Revoked.AddDays(9));

        var ranked = RelinkSuggestionRanking.Rank(dead, new[] { teamOld, ownOld, teamNew, ownNew }, Caller);

        ranked.Select(r => r.CredentialId).ShouldBe(new[] { ownNew.Id, ownOld.Id, teamNew.Id, teamOld.Id });
        ranked.Select(r => r.Kind).ShouldBe(new[] { RelinkSuggestionKind.YourConnection, RelinkSuggestionKind.YourConnection, RelinkSuggestionKind.TeamServiceToken, RelinkSuggestionKind.TeamServiceToken });
    }

    [Fact]
    public void A_connection_made_before_the_credential_was_disconnected_is_flagged()
    {
        var dead = Dead();

        var ranked = RelinkSuggestionRanking.Rank(dead, new[] { Own(Revoked.AddMinutes(-1)), Own(Revoked.AddMinutes(1)) }, Caller);

        ranked.Select(r => r.ConnectedBeforeDisconnect).ShouldBe(new[] { false, true }, "newest first: the one made after is not the connect-then-disconnect shape");
    }

    [Fact]
    public void Nothing_is_flagged_for_a_credential_that_is_still_live()
    {
        var live = Own(Revoked.AddDays(-60));
        var other = Own(Revoked.AddDays(-30));

        var ranked = RelinkSuggestionRanking.Rank(live, new[] { other }, Caller);

        ranked.Select(r => r.CredentialId).ShouldBe(new[] { other.Id });
        ranked.ShouldAllBe(r => !r.ConnectedBeforeDisconnect, "nothing has been disconnected yet");
    }

    [Fact]
    public void A_live_credential_may_hand_its_bindings_only_to_another_connection_of_its_own_owner()
    {
        // A Disconnect of a live credential moves its repositories to the SAME owner's other connection; the relink rule
        // (the caller's own, or the team's) belongs to a dead credential. Offered to the owner, the target is theirs; offered
        // to an Admin asking about a teammate's credential, it is the teammate's — never the Admin's own.
        var live = With(Own(Revoked.AddDays(-5)), c => c.OwnerUserId = Teammate);
        var owners = With(Own(Revoked.AddDays(-1)), c => c.OwnerUserId = Teammate);
        var callers = Own(Revoked.AddDays(-2));
        var team = Team_(Revoked.AddDays(-3));
        var someoneElses = With(Own(Revoked.AddDays(-4)), c => c.OwnerUserId = Guid.NewGuid());

        var asAdmin = RelinkSuggestionRanking.Rank(live, new[] { callers, team, owners, someoneElses }, Caller);

        asAdmin.Select(r => r.CredentialId).ShouldBe(new[] { owners.Id });
        asAdmin[0].Kind.ShouldBe(RelinkSuggestionKind.OwnersOtherConnection);

        var asOwner = RelinkSuggestionRanking.Rank(live, new[] { callers, team, owners, someoneElses }, Teammate);

        asOwner.Select(r => r.CredentialId).ShouldBe(new[] { owners.Id });
        asOwner[0].Kind.ShouldBe(RelinkSuggestionKind.YourConnection);
    }

    [Fact]
    public void A_live_team_token_hands_its_bindings_only_to_another_team_token()
    {
        var live = Team_(Revoked.AddDays(-5));
        var otherTeam = Team_(Revoked.AddDays(-1));

        RelinkSuggestionRanking.Rank(live, new[] { Own(Revoked), otherTeam }, Caller).Select(r => r.CredentialId).ShouldBe(new[] { otherTeam.Id });
    }

    [Fact]
    public void Two_credentials_have_the_same_owner_when_person_and_ownership_both_match()
    {
        RelinkSuggestionRanking.HaveTheSameOwner(Team_(Revoked), Team_(Revoked)).ShouldBeTrue("two team tokens: no owner on either");
        RelinkSuggestionRanking.HaveTheSameOwner(Own(Revoked), Own(Revoked)).ShouldBeTrue();
        RelinkSuggestionRanking.HaveTheSameOwner(Own(Revoked), With(Own(Revoked), c => c.OwnerUserId = Teammate)).ShouldBeFalse();
        RelinkSuggestionRanking.HaveTheSameOwner(Team_(Revoked), With(Own(Revoked), c => c.OwnerUserId = null)).ShouldBeFalse("an owner-less personal token is not a team token");
    }

    public enum Excluded { ATeammatesPersonalToken, ARevokedConnection, ARetiredConnection, AnotherInstance, AnotherTeam, TheCredentialItself, APersonalTokenWithNoOwner }

    [Theory]
    [InlineData(Excluded.ATeammatesPersonalToken)]
    [InlineData(Excluded.ARevokedConnection)]
    [InlineData(Excluded.ARetiredConnection)]
    [InlineData(Excluded.AnotherInstance)]
    [InlineData(Excluded.AnotherTeam)]
    [InlineData(Excluded.TheCredentialItself)]
    [InlineData(Excluded.APersonalTokenWithNoOwner)]
    public void A_credential_the_relink_would_refuse_is_never_offered(Excluded shape)
    {
        var dead = Dead();
        var kept = Own(Revoked.AddDays(1));
        var candidate = shape switch
        {
            Excluded.ATeammatesPersonalToken => With(Own(Revoked), c => c.OwnerUserId = Teammate),
            Excluded.ARevokedConnection => With(Own(Revoked), c => c.Status = CredentialStatus.Revoked),
            Excluded.ARetiredConnection => With(Own(Revoked), c => c.DeletedDate = Revoked),
            Excluded.AnotherInstance => With(Own(Revoked), c => c.ProviderInstanceId = Guid.NewGuid()),
            Excluded.AnotherTeam => With(Own(Revoked), c => c.TeamId = Guid.NewGuid()),
            Excluded.TheCredentialItself => dead,
            Excluded.APersonalTokenWithNoOwner => With(Own(Revoked), c => c.OwnerUserId = null),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "a shape the test does not build")
        };

        RelinkSuggestionRanking.Rank(dead, new[] { candidate, kept }, Caller).Select(r => r.CredentialId).ShouldBe(new[] { kept.Id }, $"{shape}");
    }

    [Fact]
    public void A_credential_on_a_sibling_connection_is_offered_only_when_the_caller_names_the_connections_in_reach_and_after_the_same_connections()
    {
        var sibling = Guid.NewGuid();
        var dead = Dead();
        var onSibling = With(Own(Revoked.AddDays(9)), c => c.ProviderInstanceId = sibling);
        var here = Own(Revoked.AddDays(1));
        var candidates = new[] { onSibling, here };

        RelinkSuggestionRanking.Rank(dead, candidates, Caller).Select(r => r.CredentialId).ShouldBe(new[] { here.Id }, "the connections in reach are the caller's to name; without them only the credential's own is");
        RelinkSuggestionRanking.Rank(dead, candidates, Caller, new HashSet<Guid> { Instance, sibling }).Select(r => r.CredentialId).ShouldBe(new[] { here.Id, onSibling.Id }, "the same connection first, though the sibling's credential is the newer");
        RelinkSuggestionRanking.Rank(dead, candidates, Caller, new HashSet<Guid> { Instance, sibling }).Single(r => r.CredentialId == onSibling.Id).ProviderInstanceId.ShouldBe(sibling);
    }

    [Fact]
    public void A_connection_that_is_not_in_reach_is_never_offered_whatever_else_is_true_of_it()
    {
        var elsewhere = With(Own(Revoked.AddDays(1)), c => c.ProviderInstanceId = Guid.NewGuid());

        RelinkSuggestionRanking.Rank(Dead(), new[] { elsewhere }, Caller, new HashSet<Guid> { Instance }).ShouldBeEmpty();
    }

    [Fact]
    public void A_repository_is_offered_every_live_credential_the_caller_may_bind_with_whether_or_not_the_one_it_is_leaving_is_dead()
    {
        var sibling = Guid.NewGuid();
        var leaving = With(Own(Revoked), c => c.Id = Guid.NewGuid());
        var own = Own(Revoked.AddDays(-1));
        var team = Team_(Revoked.AddDays(5));
        var ownOnSibling = With(Own(Revoked.AddDays(9)), c => c.ProviderInstanceId = sibling);
        var teammates = With(Own(Revoked), c => c.OwnerUserId = Teammate);
        var revoked = With(Own(Revoked), c => c.Status = CredentialStatus.Revoked);

        var offered = RelinkSuggestionRanking.RankForRepository(Instance, leaving, new[] { leaving, ownOnSibling, teammates, revoked, team, own }, Caller, Now);

        offered.Select(r => r.CredentialId).ShouldBe(new[] { own.Id, team.Id, ownOnSibling.Id }, "the leaving credential is left out although it is live; the same connection's come first, the caller's own before the team's");
        offered.Select(r => r.Kind).ShouldBe(new[] { RelinkSuggestionKind.YourConnection, RelinkSuggestionKind.TeamServiceToken, RelinkSuggestionKind.YourConnection });
    }

    [Theory]
    [InlineData(AuthType.Pat, -3, false)]                // past its recorded expiry: Active by status, rejected by the health model
    [InlineData(AuthType.ProjectAccessToken, 0, false)]  // expiring at this very instant has expired (HasExpired reads <=)
    [InlineData(AuthType.GroupAccessToken, -1, false)]
    [InlineData(AuthType.Pat, 3, true)]                  // about to expire still authenticates, and the chip says so
    [InlineData(AuthType.Pat, null, true)]               // no recorded expiry
    [InlineData(AuthType.OAuth, -3, true)]               // an OAuth credential's expiry is its access token's, which renews itself
    public void A_repository_is_offered_a_credential_only_while_the_health_model_says_it_can_authenticate(AuthType authType, int? daysFromNow, bool offered)
    {
        var own = With(Own(Revoked), c => { c.AuthType = authType; c.ExpiresDate = daysFromNow is { } days ? Now.AddDays(days) : null; });
        var team = With(Team_(Revoked), c => { c.AuthType = authType == AuthType.OAuth ? AuthType.GroupAccessToken : authType; c.ExpiresDate = authType != AuthType.OAuth && daysFromNow is { } days ? Now.AddDays(days) : null; });   // a team token is never an OAuth one
        var healthy = Own(Revoked.AddDays(-1));

        var ids = RelinkSuggestionRanking.RankForRepository(Instance, leaving: null, new[] { own, team, healthy }, Caller, Now).Select(r => r.CredentialId).ToHashSet();

        ids.Contains(own.Id).ShouldBe(offered, $"{authType} expiring {daysFromNow} days from now: its own row");
        ids.Contains(team.Id).ShouldBe(offered, "the team's token, on the same terms");
        ids.ShouldContain(healthy.Id, "a healthy credential is offered whatever its neighbours' state");
    }

    [Fact]
    public void A_repository_with_no_credential_to_leave_flags_nothing_as_connected_before_a_disconnect()
    {
        var offered = RelinkSuggestionRanking.RankForRepository(Instance, leaving: null, new[] { Own(Revoked.AddDays(-30)) }, Caller, Now);

        offered.Select(r => r.ConnectedBeforeDisconnect).ShouldBe(new[] { false });
    }

    [Fact]
    public void Credentials_made_at_the_same_instant_keep_a_stable_order()
    {
        var dead = Dead();
        var a = Own(Revoked.AddDays(1));
        var b = Own(Revoked.AddDays(1));

        var first = RelinkSuggestionRanking.Rank(dead, new[] { a, b }, Caller).Select(r => r.CredentialId);
        var second = RelinkSuggestionRanking.Rank(dead, new[] { b, a }, Caller).Select(r => r.CredentialId);

        first.ShouldBe(second, "the order cannot depend on the order the database happened to return them in");
    }

    [Fact]
    public void Nothing_to_offer_is_an_empty_list_not_an_error()
    {
        RelinkSuggestionRanking.Rank(Dead(), Array.Empty<Credential>(), Caller).ShouldBeEmpty();
    }

    private static Credential Dead() => Make(Guid.NewGuid(), Teammate, CredentialOwnership.Personal, CredentialStatus.Revoked, Revoked, Revoked);

    private static Credential Own(DateTimeOffset created) => Make(Guid.NewGuid(), Caller, CredentialOwnership.Personal, CredentialStatus.Active, created, created);

    private static Credential Team_(DateTimeOffset created) => Make(Guid.NewGuid(), null, CredentialOwnership.TeamService, CredentialStatus.Active, created, created);

    private static Credential Make(Guid id, Guid? owner, CredentialOwnership ownership, CredentialStatus status, DateTimeOffset created, DateTimeOffset modified) =>
        new() { Id = id, TeamId = Team, ProviderInstanceId = Instance, OwnerUserId = owner, Ownership = ownership, Status = status, AuthType = AuthType.Pat, DisplayName = $"{ownership} {id:N}"[..16], CreatedDate = created, LastModifiedDate = modified };

    private static Credential With(Credential credential, Action<Credential> change)
    {
        change(credential);

        return credential;
    }
}
