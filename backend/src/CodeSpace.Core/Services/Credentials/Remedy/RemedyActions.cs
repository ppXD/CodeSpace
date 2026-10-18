using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Credentials;
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

        return (facts.CredentialRetired || repair is null ? new[] { relink, repair } : new[] { repair, relink }).OfType<RemedyAction>();
    }

    /// <summary>A renewal the provider did not answer (a refusal of the grant is <c>credential_needs_reconnect</c>) and a standing lookup that got no answer say nothing against the credential, so nobody is sent to reconnect, replace or move off it for them.</summary>
    private static bool SaysNothingAboutTheCredential(string code, RemedyFacts facts) =>
        code == FailureCodes.OAuthExchangeFailed || (code == WorkflowRunRequestRejectionReasons.AuthorStandingUnknown && facts.Lookup == StandingLookupCauses.NoAnswer);

    /// <summary>Reconnecting a sign-in and replacing a pasted token are two endpoints with two rules (<see cref="CallerStanding"/>), so each is enabled by its own.</summary>
    private static RemedyAction RepairCredential(RemedyFacts facts, CallerStanding caller) => facts.CredentialIsStaticToken ? ReplaceToken(facts, caller) : Reconnect(facts, caller);

    private static RemedyAction ReplaceToken(RemedyFacts facts, CallerStanding caller) => new()
    {
        Kind = RemedyActionKind.ReplaceToken,
        Label = "Replace token",
        Enabled = caller.MayReplaceTheToken,
        DisabledReason = caller.MayReplaceTheToken ? null : facts.CredentialIsTeamService ? NeedsAdmin : $"Only {facts.OwnerName ?? "the person who connected it"}, or a team Admin, can do this."
    };

    private static RemedyAction Reconnect(RemedyFacts facts, CallerStanding caller) => new()
    {
        Kind = RemedyActionKind.Reconnect,
        Label = facts.Provider is { Length: > 0 } provider ? $"Reconnect {provider}" : "Reconnect",
        Enabled = caller.MayReconnectTheCredential,
        DisabledReason = caller.MayReconnectTheCredential ? null : NeedsAdmin
    };

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
