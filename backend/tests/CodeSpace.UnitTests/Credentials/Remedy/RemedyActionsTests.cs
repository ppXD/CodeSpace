using System.Linq;
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

    [Theory]
    // role, owns the credential, may re-link, may reconnect (OAuth init), may replace a token, may launch runs
    [InlineData(TeamRole.Owner, false, true, true, true, true)]
    [InlineData(TeamRole.Admin, false, true, true, true, true)]
    [InlineData(TeamRole.Member, false, false, false, false, true)]
    [InlineData(TeamRole.Member, true, false, false, true, true)]    // POST /api/credentials/oauth/init needs credentials.manage whoever owns the row; only the token replace passes an owner on membership alone
    [InlineData(TeamRole.Viewer, false, false, false, false, false)]
    [InlineData(TeamRole.Viewer, true, false, false, true, false)]   // a Viewer's own token is still theirs to replace — and their sign-in is not theirs to reconnect
    public void The_caller_standing_follows_the_rules_of_the_endpoints_behind_the_buttons(TeamRole role, bool owns, bool relink, bool reconnect, bool replace, bool launch)
    {
        var standing = CallerStanding.Of(role, owns);

        standing.MayManageRepositories.ShouldBe(relink);
        standing.MayReconnectTheCredential.ShouldBe(reconnect);
        standing.MayReplaceTheToken.ShouldBe(replace);
        standing.MayLaunchRuns.ShouldBe(launch);
    }

    [Theory]
    [InlineData(FailureCodes.CredentialDisconnected)]
    [InlineData(FailureCodes.CredentialNeedsReconnect)]
    [InlineData(FailureCodes.ProviderUnauthorized)]
    [InlineData(FailureCodes.GitCredentialRejected)]
    public void An_owner_is_offered_every_credential_fix_enabled_and_a_live_credential_is_repaired_in_place_first(string code)
    {
        var actions = RemedyActions.Build(code, Facts(), CallerStanding.Of(TeamRole.Owner, ownsTheCredential: false), Everything);

        actions.Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.Reconnect, RemedyActionKind.Relink, RemedyActionKind.OpenProviders, RemedyActionKind.Rerun }, "repairing in place moves nothing, so it leads; re-linking is the fallback");
        actions.Where(a => a.Kind != RemedyActionKind.Rerun).ShouldAllBe(a => a.Enabled);
    }

    [Fact]
    public void A_pasted_token_is_replaced_rather_than_reconnected()
    {
        var actions = RemedyActions.Build(FailureCodes.CredentialNeedsReconnect, Facts(staticToken: true), CallerStanding.Of(TeamRole.Owner, false), Everything);

        actions.Select(a => a.Kind).ShouldContain(RemedyActionKind.ReplaceToken);
        actions.Select(a => a.Kind).ShouldNotContain(RemedyActionKind.Reconnect);
    }

    [Fact]
    public void A_retired_credential_cannot_be_reconnected_so_re_linking_is_all_there_is()
    {
        var actions = RemedyActions.Build(FailureCodes.CredentialDisconnected, Facts(retired: true), CallerStanding.Of(TeamRole.Owner, false), Everything);

        actions.Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.Relink, RemedyActionKind.OpenProviders, RemedyActionKind.Rerun });
    }

    [Fact]
    public void A_member_who_does_not_own_the_credential_sees_the_fix_disabled_with_who_can_do_it()
    {
        var actions = RemedyActions.Build(FailureCodes.CredentialNeedsReconnect, Facts(), CallerStanding.Of(TeamRole.Member, ownsTheCredential: false), Everything);

        var reconnect = Of(actions, RemedyActionKind.Reconnect);
        reconnect.Enabled.ShouldBeFalse();
        reconnect.DisabledReason.ShouldBe("Needs the Admin or Owner role.", "the sign-in flow is a credentials.manage request whoever the credential belongs to");

        var replace = Of(RemedyActions.Build(FailureCodes.CredentialNeedsReconnect, Facts(staticToken: true), CallerStanding.Of(TeamRole.Member, ownsTheCredential: false), Everything), RemedyActionKind.ReplaceToken);
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
        var actions = RemedyActions.Build(FailureCodes.CredentialNeedsReconnect, Facts(staticToken: true), CallerStanding.Of(TeamRole.Member, ownsTheCredential: true), Everything);

        Of(actions, RemedyActionKind.ReplaceToken).Enabled.ShouldBeTrue("the token replace endpoint passes the credential's own person on membership alone");
        Of(actions, RemedyActionKind.Relink).Enabled.ShouldBeFalse();
    }

    [Theory]
    [InlineData(TeamRole.Member)]
    [InlineData(TeamRole.Viewer)]
    public void A_non_admin_whose_own_oauth_sign_in_it_is_is_not_offered_a_reconnect_that_the_endpoint_would_refuse(TeamRole role)
    {
        var actions = RemedyActions.Build(FailureCodes.CredentialNeedsReconnect, Facts(), CallerStanding.Of(role, ownsTheCredential: true), Everything);

        var reconnect = Of(actions, RemedyActionKind.Reconnect);
        reconnect.Enabled.ShouldBeFalse("InitCredentialOAuthCommand requires credentials.manage; the owner-on-membership rule belongs to the token replace only");
        reconnect.DisabledReason.ShouldBe("Needs the Admin or Owner role.");
    }

    [Fact]
    public void A_team_service_credential_names_the_role_that_can_repair_it_not_a_person()
    {
        var actions = RemedyActions.Build(FailureCodes.CredentialNeedsReconnect, Facts(staticToken: true, teamService: true), CallerStanding.Of(TeamRole.Member, false), Everything);

        Of(actions, RemedyActionKind.ReplaceToken).DisabledReason.ShouldBe("Needs the Admin or Owner role.");
    }

    [Fact]
    public void A_viewer_gets_nothing_enabled_but_the_providers_dialog()
    {
        var actions = RemedyActions.Build(FailureCodes.CredentialDisconnected, Facts(), CallerStanding.Of(TeamRole.Viewer, false), Everything);

        actions.Where(a => a.Enabled).Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.OpenProviders });
        Of(actions, RemedyActionKind.Rerun).DisabledReason.ShouldBe("Fix the connection first, then run it again.");
    }

    [Fact]
    public void A_redirect_is_fixed_by_editing_the_providers_address_not_by_touching_a_credential()
    {
        var facts = Facts() with { RedirectFix = RedirectRepair.EditAddress };
        var actions = RemedyActions.Build(FailureCodes.ProviderBaseUrlRedirects, facts, CallerStanding.Of(TeamRole.Admin, false), Everything);

        actions.Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.EditProviderUrl, RemedyActionKind.OpenProviders, RemedyActionKind.Rerun });
        Of(actions, RemedyActionKind.EditProviderUrl).Enabled.ShouldBeTrue();
        RemedyActions.Build(FailureCodes.ProviderBaseUrlRedirects, facts, CallerStanding.Of(TeamRole.Member, false), Everything).Single(a => a.Kind == RemedyActionKind.EditProviderUrl).Enabled.ShouldBeFalse();
    }

    [Theory]
    [InlineData(RedirectRepair.DisconnectFirst)]
    [InlineData(RedirectRepair.HostSide)]
    public void A_redirect_the_server_would_refuse_to_edit_away_offers_no_edit_button_at_all(RedirectRepair fix)
    {
        var actions = RemedyActions.Build(FailureCodes.ProviderBaseUrlRedirects, Facts() with { RedirectFix = fix }, CallerStanding.Of(TeamRole.Owner, false), Everything);

        actions.Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.OpenProviders, RemedyActionKind.Rerun }, "a button that the endpoint answers 409 to is not a fix; the cause says what is");
    }

    [Fact]
    public void Once_what_failed_works_again_running_it_again_leads_and_is_enabled_for_whoever_may_launch()
    {
        var actions = RemedyActions.Build(FailureCodes.CredentialDisconnected, Facts(resolved: true), CallerStanding.Of(TeamRole.Member, false), Everything);

        actions[0].Kind.ShouldBe(RemedyActionKind.Rerun);
        actions[0].Enabled.ShouldBeTrue();
        RemedyActions.Build(FailureCodes.CredentialDisconnected, Facts(resolved: true), CallerStanding.Of(TeamRole.Viewer, false), Everything)[0].DisabledReason.ShouldBe("Needs the Member role or above.");
    }

    [Fact]
    public void A_provider_401_on_a_row_that_looks_fine_leaves_run_it_again_open_because_nobody_can_show_the_fix()
    {
        var actions = RemedyActions.Build(FailureCodes.ProviderUnauthorized, Facts(observable: false), CallerStanding.Of(TeamRole.Member, false), Everything);

        Of(actions, RemedyActionKind.Rerun).Enabled.ShouldBeTrue();
    }

    [Fact]
    public void A_failure_that_says_nothing_about_the_credential_sends_nobody_to_repair_it()
    {
        // A renewal the provider did not answer (a final refusal would have been credential_needs_reconnect), and a standing lookup that got no answer.
        var renewal = RemedyActions.Build(FailureCodes.OAuthExchangeFailed, Facts(observable: false), CallerStanding.Of(TeamRole.Owner, false), Everything);
        var lookup = RemedyActions.Build(WorkflowRunRequestRejectionReasons.AuthorStandingUnknown, Facts() with { Lookup = StandingLookupCauses.NoAnswer }, CallerStanding.Of(TeamRole.Owner, false), Everything with { OffersRerun = false });

        renewal.Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.OpenProviders, RemedyActionKind.Rerun }, "run it again; reconnecting a credential that was not refused is what to try only if that keeps failing");
        Of(renewal, RemedyActionKind.Rerun).Enabled.ShouldBeTrue("nothing is recorded that could show it fixed, so it is not held back");
        lookup.Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.OpenProviders });
    }

    [Fact]
    public void A_standing_lookup_with_no_credential_to_ask_with_offers_to_link_one_and_nothing_to_reconnect()
    {
        var subjects = new RemedyActions.ActionSubjects(HasCredential: false, HasRepositories: true, HasProviderInstance: true, OffersRerun: false);

        RemedyActions.Build(WorkflowRunRequestRejectionReasons.AuthorStandingUnknown, Facts() with { Lookup = StandingLookupCauses.NoCredential }, CallerStanding.Of(TeamRole.Owner, false), subjects).Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.Relink, RemedyActionKind.OpenProviders });
    }

    [Fact]
    public void A_refused_trigger_delivery_has_no_run_to_run_again()
    {
        var subjects = Everything with { OffersRerun = false };

        RemedyActions.Build(WorkflowRunRequestRejectionReasons.CredentialRejected, Facts(), CallerStanding.Of(TeamRole.Owner, false), subjects).Select(a => a.Kind).ShouldNotContain(RemedyActionKind.Rerun);
    }

    [Fact]
    public void Actions_with_nothing_to_act_on_are_not_offered()
    {
        var subjects = new RemedyActions.ActionSubjects(HasCredential: false, HasRepositories: false, HasProviderInstance: false, OffersRerun: true);

        RemedyActions.Build(FailureCodes.ProviderUnauthorized, Facts(), CallerStanding.Of(TeamRole.Owner, false), subjects).Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.OpenProviders, RemedyActionKind.Rerun });
    }

    [Fact]
    public void Several_repositories_make_the_re_link_say_so()
    {
        var actions = RemedyActions.Build(FailureCodes.CredentialDisconnected, Facts(retired: true) with { RepositoryCount = 3 }, CallerStanding.Of(TeamRole.Admin, false), Everything);

        Of(actions, RemedyActionKind.Relink).Label.ShouldBe("Re-link repositories");
    }
}
