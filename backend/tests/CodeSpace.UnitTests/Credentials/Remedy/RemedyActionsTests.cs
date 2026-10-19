using System.Linq;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Services.Credentials.Remedy;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.UnitTests.Credentials.Remedy;

/// <summary>
/// What a remedy offers, and to whom — the per-code, per-role matrix. A button must be enabled exactly when the endpoint behind
/// it would accept the request, and disabled with a reason (never hidden) when it would not, so a person who cannot fix a
/// connection still learns that it is fixable and by whom.
/// </summary>
[Trait("Category", "Unit")]
public class RemedyActionsTests
{
    private static readonly RemedyActions.ActionSubjects Everything = new(HasCredential: true, HasRepositories: true, HasProviderInstance: true, OffersRerun: true);

    private static RemedyFacts Facts(bool staticToken = false, bool retired = false, bool resolved = false, bool teamService = false, bool observable = true) => new()
    {
        Provider = "GitLab", CredentialName = "GitLab · nacho", OwnerName = "Nacho", CredentialIsStaticToken = staticToken, CredentialRetired = retired, CredentialIsTeamService = teamService,
        Repositories = "acme/api", RepositoryCount = 1, Resolved = resolved, FixIsObservable = observable, RedirectsTo = "https://gitlab.example.com"
    };

    private static RemedyAction Of(IReadOnlyList<RemedyAction> actions, RemedyActionKind kind) => actions.Single(a => a.Kind == kind);

    /// <summary>The caller's standing toward a credential whose person is a Member — below an Admin, so the rank reach over them never speaks unless a test says whose it is. A credential's own person stands as the role they are asking in.</summary>
    private static CallerStanding Standing(TeamRole role, bool ownsTheCredential, TeamRole person = TeamRole.Member) => CallerStanding.Of(role, new CredentialPerson(ownsTheCredential, ownsTheCredential ? role : person));

    [Theory]
    // role, owns the credential, the role of the credential's person, may re-link, may replace its token, may reconnect its sign-in, may launch runs
    [InlineData(TeamRole.Owner, false, TeamRole.Member, true, true, true, true)]
    [InlineData(TeamRole.Owner, false, TeamRole.Admin, true, true, true, true)]
    [InlineData(TeamRole.Owner, false, TeamRole.Owner, true, true, false, true)]      // reconnecting acts on the PERSON: never across, never above
    [InlineData(TeamRole.Admin, false, TeamRole.Member, true, true, true, true)]
    [InlineData(TeamRole.Admin, false, TeamRole.Admin, true, true, false, true)]
    [InlineData(TeamRole.Admin, false, TeamRole.Owner, true, true, false, true)]      // while replacing a pasted token has no such reach: the endpoint asks only credentials.manage
    [InlineData(TeamRole.Member, false, TeamRole.Member, false, false, false, true)]
    [InlineData(TeamRole.Member, true, TeamRole.Member, false, true, true, true)]     // a credential's own person repairs it on membership alone, either way
    [InlineData(TeamRole.Viewer, false, TeamRole.Member, false, false, false, false)]
    [InlineData(TeamRole.Viewer, true, TeamRole.Viewer, false, true, true, false)]    // a Viewer's own credential is still theirs to put right
    [InlineData(TeamRole.Admin, true, TeamRole.Admin, true, true, true, true)]        // an Admin reaches themselves
    [InlineData(TeamRole.Owner, true, TeamRole.Owner, true, true, true, true)]
    public void The_caller_standing_follows_the_rules_of_the_endpoints_behind_the_buttons(TeamRole role, bool owns, TeamRole person, bool relink, bool replace, bool reconnect, bool launch)
    {
        var standing = Standing(role, owns, person);

        standing.MayManageRepositories.ShouldBe(relink);
        standing.MayReplaceTheToken.ShouldBe(replace);
        standing.MayReconnectTheCredential.ShouldBe(reconnect);
        standing.MayLaunchRuns.ShouldBe(launch);
    }

    [Theory]
    [InlineData(TeamRole.Owner)]
    [InlineData(TeamRole.Admin)]
    public void A_person_who_left_the_team_is_nobodys_to_repair_not_even_a_managers(TeamRole role)
    {
        // The services refuse both: a token cannot be put into a credential whose person has no standing, and nobody can sign in as them.
        var standing = CallerStanding.Of(role, new CredentialPerson(IsCaller: false, Standing: null));

        standing.MayReplaceTheToken.ShouldBeFalse();
        standing.MayReconnectTheCredential.ShouldBeFalse();
        standing.MayManageRepositories.ShouldBeTrue("moving what leans on it to another credential is still the Admin's");
    }

    [Theory]
    [InlineData(TeamRole.Owner, true, false)]
    [InlineData(TeamRole.Admin, true, false)]
    [InlineData(TeamRole.Member, false, false)]
    public void A_team_service_credential_has_a_token_to_replace_and_no_sign_in_to_reconnect(TeamRole role, bool replace, bool reconnect)
    {
        var standing = CallerStanding.Of(role, person: null);

        standing.MayReplaceTheToken.ShouldBe(replace);
        standing.MayReconnectTheCredential.ShouldBe(reconnect);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Repairing_a_credential_means_what_its_kind_means_and_the_two_answers_come_from_one_standing(bool staticToken)
    {
        var standing = Standing(TeamRole.Admin, ownsTheCredential: false, person: TeamRole.Admin);   // may replace a token, may not reconnect a peer's sign-in

        standing.MayRepair(staticToken).ShouldBe(staticToken);
    }

    [Theory]
    [InlineData(FailureCodes.CredentialDisconnected)]
    [InlineData(FailureCodes.CredentialNeedsReconnect)]
    [InlineData(FailureCodes.ProviderUnauthorized)]
    [InlineData(FailureCodes.GitCredentialRejected)]
    public void An_owner_is_offered_every_credential_fix_enabled_and_a_live_credential_is_repaired_in_place_first(string code)
    {
        var actions = RemedyActions.Build(code, Facts(), Standing(TeamRole.Owner, ownsTheCredential: false), Everything);

        actions.Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.Reconnect, RemedyActionKind.Relink, RemedyActionKind.OpenProviders, RemedyActionKind.Rerun }, "repairing in place moves nothing, so it leads; re-linking is the fallback");
        actions.Where(a => a.Kind != RemedyActionKind.Rerun).ShouldAllBe(a => a.Enabled);
    }

    [Fact]
    public void A_pasted_token_is_replaced_rather_than_reconnected()
    {
        var actions = RemedyActions.Build(FailureCodes.CredentialNeedsReconnect, Facts(staticToken: true), Standing(TeamRole.Owner, false), Everything);

        actions.Select(a => a.Kind).ShouldContain(RemedyActionKind.ReplaceToken);
        actions.Select(a => a.Kind).ShouldNotContain(RemedyActionKind.Reconnect);
    }

    [Fact]
    public void A_retired_credential_cannot_be_reconnected_so_re_linking_is_all_there_is()
    {
        var actions = RemedyActions.Build(FailureCodes.CredentialDisconnected, Facts(retired: true), Standing(TeamRole.Owner, false), Everything);

        actions.Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.Relink, RemedyActionKind.OpenProviders, RemedyActionKind.Rerun });
    }

    [Fact]
    public void A_member_who_does_not_own_the_credential_sees_the_fix_disabled_with_who_can_do_it()
    {
        var actions = RemedyActions.Build(FailureCodes.CredentialNeedsReconnect, Facts(), Standing(TeamRole.Member, ownsTheCredential: false), Everything);

        var reconnect = Of(actions, RemedyActionKind.Reconnect);
        reconnect.Enabled.ShouldBeFalse();
        reconnect.DisabledReason.ShouldBe("Only Nacho, or a team Admin, can do this.", "someone else's sign-in is the credentials.manage bar, the same as someone else's pasted token");

        var replace = Of(RemedyActions.Build(FailureCodes.CredentialNeedsReconnect, Facts(staticToken: true), Standing(TeamRole.Member, ownsTheCredential: false), Everything), RemedyActionKind.ReplaceToken);
        replace.Enabled.ShouldBeFalse();
        replace.DisabledReason.ShouldBe("Only Nacho, or a team Admin, can do this.");

        var relink = Of(actions, RemedyActionKind.Relink);
        relink.Enabled.ShouldBeFalse();
        relink.DisabledReason.ShouldBe("Needs the Admin or Owner role.");

        Of(actions, RemedyActionKind.OpenProviders).Enabled.ShouldBeTrue("looking at the connections is every member's");
    }

    [Fact]
    public void A_member_whose_own_pasted_token_it_is_can_replace_it_but_not_re_link()
    {
        var actions = RemedyActions.Build(FailureCodes.CredentialNeedsReconnect, Facts(staticToken: true), Standing(TeamRole.Member, ownsTheCredential: true), Everything);

        Of(actions, RemedyActionKind.ReplaceToken).Enabled.ShouldBeTrue("the token replace endpoint passes the credential's own person on membership alone");
        Of(actions, RemedyActionKind.Relink).Enabled.ShouldBeFalse();
    }

    [Theory]
    [InlineData(TeamRole.Member)]
    [InlineData(TeamRole.Viewer)]
    public void A_credentials_own_person_is_offered_the_reconnect_of_their_own_sign_in_whatever_their_role(TeamRole role)
    {
        var actions = RemedyActions.Build(FailureCodes.CredentialNeedsReconnect, Facts(), Standing(role, ownsTheCredential: true), Everything);

        var reconnect = Of(actions, RemedyActionKind.Reconnect);
        reconnect.Enabled.ShouldBeTrue("the owner of a Personal credential re-authorizes it on membership alone");
        reconnect.DisabledReason.ShouldBeNull();
    }

    [Fact]
    public void An_admin_is_told_an_owners_sign_in_is_the_owners_alone_while_their_pasted_token_stays_the_admins_to_replace()
    {
        var facts = Facts() with { OwnerRole = TeamRole.Owner };
        var reconnect = Of(RemedyActions.Build(FailureCodes.CredentialNeedsReconnect, facts, Standing(TeamRole.Admin, false, TeamRole.Owner), Everything), RemedyActionKind.Reconnect);

        reconnect.Enabled.ShouldBeFalse("an Admin does not reach an Owner, and a sign-in is signing in AS the person");
        reconnect.DisabledReason.ShouldBe("Only Nacho can reconnect their own sign-in.", "naming the Admin role would send an Admin to ask themselves");

        var replace = Of(RemedyActions.Build(FailureCodes.CredentialNeedsReconnect, facts with { CredentialIsStaticToken = true }, Standing(TeamRole.Admin, false, TeamRole.Owner), Everything), RemedyActionKind.ReplaceToken);

        replace.Enabled.ShouldBeTrue("the token replace endpoint asks credentials.manage and nothing about the person's rank");
    }

    [Fact]
    public void A_peer_admins_sign_in_is_the_admins_own_or_a_team_owners()
    {
        var facts = Facts() with { OwnerRole = TeamRole.Admin };
        var reconnect = Of(RemedyActions.Build(FailureCodes.CredentialNeedsReconnect, facts, Standing(TeamRole.Admin, false, TeamRole.Admin), Everything), RemedyActionKind.Reconnect);

        reconnect.Enabled.ShouldBeFalse();
        reconnect.DisabledReason.ShouldBe("Only Nacho, or a team Owner, can reconnect this sign-in.");
        Of(RemedyActions.Build(FailureCodes.CredentialNeedsReconnect, facts, Standing(TeamRole.Owner, false, TeamRole.Admin), Everything), RemedyActionKind.Reconnect).Enabled.ShouldBeTrue("an Owner outranks an Admin");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_credential_whose_person_left_is_repaired_by_nobody_says_so_and_leads_with_moving_off_it(bool staticToken)
    {
        var facts = Facts(staticToken: staticToken) with { CredentialOwnerLeft = true };
        var actions = RemedyActions.Build(FailureCodes.CredentialNeedsReconnect, facts, CallerStanding.Of(TeamRole.Owner, new CredentialPerson(IsCaller: false, Standing: null)), Everything);

        var repair = Of(actions, staticToken ? RemedyActionKind.ReplaceToken : RemedyActionKind.Reconnect);
        repair.Enabled.ShouldBeFalse();
        repair.DisabledReason.ShouldBe("Nacho has left this team, so this cannot be repaired.");
        actions.Select(a => a.Kind).First().ShouldBe(RemedyActionKind.Relink, "like a retired credential, the one way out is to move what leans on it");
    }

    [Fact]
    public void A_team_service_sign_in_that_an_admin_cannot_reconnect_is_not_blamed_on_their_role()
    {
        var actions = RemedyActions.Build(FailureCodes.CredentialNeedsReconnect, Facts(teamService: true), CallerStanding.Of(TeamRole.Admin, person: null), Everything);

        var reconnect = Of(actions, RemedyActionKind.Reconnect);
        reconnect.Enabled.ShouldBeFalse();
        reconnect.DisabledReason.ShouldBe("A team's own credential has no one's sign-in to reconnect; replace its token instead.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_team_service_credential_names_the_role_that_can_repair_it_not_a_person(bool staticToken)
    {
        var actions = RemedyActions.Build(FailureCodes.CredentialNeedsReconnect, Facts(staticToken: staticToken, teamService: true), Standing(TeamRole.Member, false), Everything);

        Of(actions, staticToken ? RemedyActionKind.ReplaceToken : RemedyActionKind.Reconnect).DisabledReason.ShouldBe("Needs the Admin or Owner role.");
    }

    [Fact]
    public void A_viewer_gets_nothing_enabled_but_the_providers_dialog()
    {
        var actions = RemedyActions.Build(FailureCodes.CredentialDisconnected, Facts(), Standing(TeamRole.Viewer, false), Everything);

        actions.Where(a => a.Enabled).Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.OpenProviders });
        Of(actions, RemedyActionKind.Rerun).DisabledReason.ShouldBe("Fix the connection first, then run it again.");
    }

    [Fact]
    public void A_redirect_is_fixed_by_editing_the_providers_address_not_by_touching_a_credential()
    {
        var facts = Facts() with { RedirectFix = RedirectRepair.EditAddress };
        var actions = RemedyActions.Build(FailureCodes.ProviderBaseUrlRedirects, facts, Standing(TeamRole.Admin, false), Everything);

        actions.Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.EditProviderUrl, RemedyActionKind.OpenProviders, RemedyActionKind.Rerun });
        Of(actions, RemedyActionKind.EditProviderUrl).Enabled.ShouldBeTrue();
        RemedyActions.Build(FailureCodes.ProviderBaseUrlRedirects, facts, Standing(TeamRole.Member, false), Everything).Single(a => a.Kind == RemedyActionKind.EditProviderUrl).Enabled.ShouldBeFalse();
    }

    [Theory]
    [InlineData(RedirectRepair.DisconnectFirst)]
    [InlineData(RedirectRepair.HostSide)]
    public void A_redirect_the_server_would_refuse_to_edit_away_offers_no_edit_button_at_all(RedirectRepair fix)
    {
        var actions = RemedyActions.Build(FailureCodes.ProviderBaseUrlRedirects, Facts() with { RedirectFix = fix }, Standing(TeamRole.Owner, false), Everything);

        actions.Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.OpenProviders, RemedyActionKind.Rerun }, "a button that the endpoint answers 409 to is not a fix; the cause says what is");
    }

    [Fact]
    public void Once_what_failed_works_again_running_it_again_leads_and_is_enabled_for_whoever_may_launch()
    {
        var actions = RemedyActions.Build(FailureCodes.CredentialDisconnected, Facts(resolved: true), Standing(TeamRole.Member, false), Everything);

        actions[0].Kind.ShouldBe(RemedyActionKind.Rerun);
        actions[0].Enabled.ShouldBeTrue();
        RemedyActions.Build(FailureCodes.CredentialDisconnected, Facts(resolved: true), Standing(TeamRole.Viewer, false), Everything)[0].DisabledReason.ShouldBe("Needs the Member role or above.");
    }

    [Fact]
    public void A_provider_401_on_a_row_that_looks_fine_leaves_run_it_again_open_because_nobody_can_show_the_fix()
    {
        var actions = RemedyActions.Build(FailureCodes.ProviderUnauthorized, Facts(observable: false), Standing(TeamRole.Member, false), Everything);

        Of(actions, RemedyActionKind.Rerun).Enabled.ShouldBeTrue();
    }

    [Fact]
    public void A_failure_that_says_nothing_about_the_credential_sends_nobody_to_repair_it()
    {
        // A renewal the provider did not answer (a final refusal would have been credential_needs_reconnect), and a standing lookup that got no answer.
        var renewal = RemedyActions.Build(FailureCodes.OAuthExchangeFailed, Facts(observable: false), Standing(TeamRole.Owner, false), Everything);
        var lookup = RemedyActions.Build(WorkflowRunRequestRejectionReasons.AuthorStandingUnknown, Facts() with { Lookup = StandingLookupCauses.NoAnswer }, Standing(TeamRole.Owner, false), Everything with { OffersRerun = false });

        renewal.Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.OpenProviders, RemedyActionKind.Rerun }, "run it again; reconnecting a credential that was not refused is what to try only if that keeps failing");
        Of(renewal, RemedyActionKind.Rerun).Enabled.ShouldBeTrue("nothing is recorded that could show it fixed, so it is not held back");
        lookup.Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.OpenProviders });
    }

    [Fact]
    public void A_standing_lookup_with_no_credential_to_ask_with_offers_to_link_one_and_nothing_to_reconnect()
    {
        var subjects = new RemedyActions.ActionSubjects(HasCredential: false, HasRepositories: true, HasProviderInstance: true, OffersRerun: false);

        RemedyActions.Build(WorkflowRunRequestRejectionReasons.AuthorStandingUnknown, Facts() with { Lookup = StandingLookupCauses.NoCredential }, Standing(TeamRole.Owner, false), subjects).Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.Relink, RemedyActionKind.OpenProviders });
    }

    [Fact]
    public void A_refused_trigger_delivery_has_no_run_to_run_again()
    {
        var subjects = Everything with { OffersRerun = false };

        RemedyActions.Build(WorkflowRunRequestRejectionReasons.CredentialRejected, Facts(), Standing(TeamRole.Owner, false), subjects).Select(a => a.Kind).ShouldNotContain(RemedyActionKind.Rerun);
    }

    [Fact]
    public void Actions_with_nothing_to_act_on_are_not_offered()
    {
        var subjects = new RemedyActions.ActionSubjects(HasCredential: false, HasRepositories: false, HasProviderInstance: false, OffersRerun: true);

        RemedyActions.Build(FailureCodes.ProviderUnauthorized, Facts(), Standing(TeamRole.Owner, false), subjects).Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.OpenProviders, RemedyActionKind.Rerun });
    }

    [Fact]
    public void Several_repositories_make_the_re_link_say_so()
    {
        var actions = RemedyActions.Build(FailureCodes.CredentialDisconnected, Facts(retired: true) with { RepositoryCount = 3 }, Standing(TeamRole.Admin, false), Everything);

        Of(actions, RemedyActionKind.Relink).Label.ShouldBe("Re-link repositories");
    }
}
