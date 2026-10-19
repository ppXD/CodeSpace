using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Credentials.Remedy;

/// <summary>
/// The fixes a remedy offers, in the order a person should try them, each saying whether THIS caller may do it and, when not,
/// what they would need. Pure over the code, the facts and <see cref="CallerStanding"/>: the same inputs always give the same
/// list, which is what lets the per-role matrix be a unit test and keeps the button state from ever being a client's opinion.
///
/// <para>Ordering is the product's judgement, in one place: a credential that still exists is best put right IN PLACE
/// (reconnect, replace the token — nothing bound through it moves); one that was retired can only be moved off, so re-linking
/// leads. Once what failed works again, running it again leads. A disabled action is kept in the list with its reason rather than
/// dropped — a person who cannot fix it must still learn that it is fixable, and by whom.</para>
/// </summary>
public static class RemedyActions
{
    public static IReadOnlyList<RemedyAction> Build(string code, RemedyFacts facts, CallerStanding caller, ActionSubjects subjects)
    {
        var actions = new List<RemedyAction>();
        var rerun = subjects.OffersRerun ? Rerun(facts, caller) : null;

        if (facts.Resolved && rerun is not null) actions.Add(rerun);

        actions.AddRange(RepairsFor(code, facts, caller, subjects));
        actions.Add(OpenProviders());

        if (!facts.Resolved && rerun is not null) actions.Add(rerun);

        return actions;
    }

    /// <summary>What is wrong with a redirect is the provider's address — edited only when the server will accept the edit (<see cref="RedirectRepair"/>), else the cause says what works; what is wrong with anything else is a credential — repaired in place, or moved off.</summary>
    private static IEnumerable<RemedyAction> RepairsFor(string code, RemedyFacts facts, CallerStanding caller, ActionSubjects subjects)
    {
        if (code == FailureCodes.ProviderBaseUrlRedirects) return subjects.HasProviderInstance && facts.RedirectFix == RedirectRepair.EditAddress ? new[] { EditProviderAddress(caller) } : Array.Empty<RemedyAction>();

        if (SaysNothingAboutTheCredential(code, facts)) return Array.Empty<RemedyAction>();

        var repair = subjects.HasCredential && !facts.CredentialRetired ? RepairCredential(facts, caller) : null;
        var relink = subjects.HasRepositories ? Relink(facts, caller) : null;

        // A credential whose person left is waiting to be retired, as one already retired is: nothing can put it right, so moving off it leads.
        return (facts.CredentialRetired || facts.CredentialOwnerLeft || repair is null ? new[] { relink, repair } : new[] { repair, relink }).OfType<RemedyAction>();
    }

    /// <summary>A renewal the provider did not answer (a refusal of the grant is <c>credential_needs_reconnect</c>) and a standing lookup that got no answer say nothing against the credential, so nobody is sent to reconnect, replace or move off it for them.</summary>
    private static bool SaysNothingAboutTheCredential(string code, RemedyFacts facts) =>
        code == FailureCodes.OAuthExchangeFailed || (code == WorkflowRunRequestRejectionReasons.AuthorStandingUnknown && facts.Lookup == StandingLookupCauses.NoAnswer);

    /// <summary>Reconnecting a sign-in and replacing a pasted token are two endpoints with ONE bar (<see cref="CallerStanding"/>): the credential's own person, or <c>credentials.manage</c> — and the reconnect, which signs in as the person, keeps the reach over them too.</summary>
    private static RemedyAction RepairCredential(RemedyFacts facts, CallerStanding caller) => facts.CredentialIsStaticToken ? ReplaceToken(facts, caller) : Reconnect(facts, caller);

    private static RemedyAction ReplaceToken(RemedyFacts facts, CallerStanding caller) => new()
    {
        Kind = RemedyActionKind.ReplaceToken,
        Label = "Replace token",
        Enabled = caller.MayReplaceTheToken,
        DisabledReason = caller.MayReplaceTheToken ? null : ReplaceDisabledReason(facts)
    };

    private static RemedyAction Reconnect(RemedyFacts facts, CallerStanding caller) => new()
    {
        Kind = RemedyActionKind.Reconnect,
        Label = facts.Provider is { Length: > 0 } provider ? $"Reconnect {provider}" : "Reconnect",
        Enabled = caller.MayReconnectTheCredential,
        DisabledReason = caller.MayReconnectTheCredential ? null : ReconnectDisabledReason(facts, caller)
    };

    /// <summary>Whose the repair is when it is not the caller's: a person's own credential is theirs, or an Admin's; a team's has no person, so only the role is named; one whose person left is nobody's.</summary>
    private static string ReplaceDisabledReason(RemedyFacts facts) =>
        facts.CredentialOwnerLeft ? OwnerLeft(facts) : facts.CredentialIsTeamService ? NeedsAdmin : OnlyTheOwnerOrAnAdmin(facts);

    /// <summary>
    /// As <see cref="ReplaceDisabledReason"/>, and one more cause: a caller who holds <c>credentials.manage</c> (<see cref="CallerStanding.MayReplaceTheToken"/>)
    /// and is still refused is refused for the person's rank — only the person, or whoever outranks them, signs in as them.
    /// </summary>
    private static string ReconnectDisabledReason(RemedyFacts facts, CallerStanding caller)
    {
        if (facts.CredentialIsTeamService) return caller.MayReplaceTheToken ? "A team's own credential has no one's sign-in to reconnect; replace its token instead." : NeedsAdmin;

        if (facts.CredentialOwnerLeft) return OwnerLeft(facts);

        return caller.MayReplaceTheToken ? OutrankedByThePerson(facts) : OnlyTheOwnerOrAnAdmin(facts);
    }

    private static string OnlyTheOwnerOrAnAdmin(RemedyFacts facts) => $"Only {Person(facts)}, or a team Admin, can do this.";

    /// <summary>An Owner's sign-in is nobody's but their own; an Admin's is also an Owner's. Naming the Admin role to an Admin would send them to ask themselves.</summary>
    private static string OutrankedByThePerson(RemedyFacts facts) =>
        facts.OwnerRole == TeamRole.Owner ? $"Only {Person(facts)} can reconnect their own sign-in." : $"Only {Person(facts)}, or a team Owner, can reconnect this sign-in.";

    private static string OwnerLeft(RemedyFacts facts) => $"{facts.OwnerName ?? "The person who connected it"} has left this team, so this cannot be repaired.";

    private static string Person(RemedyFacts facts) => facts.OwnerName ?? "the person who connected it";

    private static RemedyAction Relink(RemedyFacts facts, CallerStanding caller) => new()
    {
        Kind = RemedyActionKind.Relink,
        Label = facts.RepositoryCount > 1 ? "Re-link repositories" : "Re-link repository",
        Enabled = caller.MayManageRepositories,
        DisabledReason = caller.MayManageRepositories ? null : NeedsAdmin
    };

    private static RemedyAction EditProviderAddress(CallerStanding caller) => new()
    {
        Kind = RemedyActionKind.EditProviderUrl,
        Label = "Edit provider address",
        Enabled = caller.MayManageRepositories,
        DisabledReason = caller.MayManageRepositories ? null : NeedsAdmin
    };

    private static RemedyAction OpenProviders() => new() { Kind = RemedyActionKind.OpenProviders, Label = "Open Providers", Enabled = true };

    /// <summary>Run it again. Held back until the fix shows only where a fix can be SEEN (<see cref="RemedyFacts.FixIsObservable"/>); a provider's 401 on a row that looks fine leaves it open, because a person may have fixed it at the provider.</summary>
    private static RemedyAction Rerun(RemedyFacts facts, CallerStanding caller)
    {
        var fixed_ = facts.Resolved || !facts.FixIsObservable;
        var enabled = fixed_ && caller.MayLaunchRuns;

        return new RemedyAction { Kind = RemedyActionKind.Rerun, Label = "Re-run", Enabled = enabled, DisabledReason = enabled ? null : !fixed_ ? "Fix the connection first, then run it again." : "Needs the Member role or above." };
    }

    private const string NeedsAdmin = "Needs the Admin or Owner role.";

    /// <summary>
    /// What the remedy has to act on — whether a credential, repositories and a provider connection could be named, and whether a
    /// run exists to run again (a refused trigger delivery has none). Actions that would have nothing to act on are not offered.
    /// </summary>
    public sealed record ActionSubjects(bool HasCredential, bool HasRepositories, bool HasProviderInstance, bool OffersRerun);
}
