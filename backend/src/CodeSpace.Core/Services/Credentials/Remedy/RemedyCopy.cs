using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Credentials.Remedy;

/// <summary>
/// The words of a remedy — a title and a cause — written in plain language that names the credential and the repository, and
/// never a token, a status table or a stack. Pure over <see cref="RemedyFacts"/>, so the sentence for every code is pinned by
/// a unit test and nothing else decides what a person is told. A fact that is not known is left out, not guessed at.
///
/// <para>The same copy serves a failed run and a refused trigger delivery: both are "this connection does not work", told for
/// where the person is standing. The codes are <see cref="FailureCodes"/> for a run and the refusal reasons of
/// <see cref="WorkflowRunRequestRejectionReasons"/> for a delivery.</para>
/// </summary>
public static class RemedyCopy
{
    public static string Title(string code, RemedyFacts facts) => Capitalise(facts.Resolved ? "This connection works again" : code switch
    {
        FailureCodes.CredentialDisconnected => facts.CredentialRetired ? "The credential this run used was retired" : "The credential this run used was disconnected",
        FailureCodes.CredentialNeedsReconnect => $"{Provider(facts)} no longer accepts the credential this run used",
        FailureCodes.ProviderUnauthorized => $"{Provider(facts)} rejected the credential for this repository",
        FailureCodes.GitCredentialRejected => $"{Provider(facts)} refused the push",
        FailureCodes.ProviderBaseUrlRedirects => "The provider's address redirects somewhere else",
        FailureCodes.OAuthExchangeFailed => "Renewing the sign-in this run used did not work",
        WorkflowRunRequestRejectionReasons.CredentialRejected => $"{Provider(facts)} refused the credential this trigger uses",
        WorkflowRunRequestRejectionReasons.AuthorStandingUnknown => StandingTitle(facts),
        _ => "A provider connection needs attention"
    });

    /// <summary>What happened, in sentences that name the credential and the repository — led, for a failure carried up from a sub-workflow's child run, by the sentence that says which run it happened in.</summary>
    public static string Cause(string code, RemedyFacts facts) => facts.Origin is { } origin ? $"{Where(origin)} {CauseOf(code, facts)}" : CauseOf(code, facts);

    /// <summary>Where a failure carried up from a child run happened: the sub-workflow's name, the run (the first eight characters of its id, as the run pages show it) and the node that ended it.</summary>
    private static string Where(RemedyOrigin origin) =>
        $"This failed in the sub-workflow “{origin.WorkflowName}” (run {origin.RunId.ToString()[..8]}){(origin.NodeId is { Length: > 0 } node ? $", at node '{node}'" : "")}.";

    private static string CauseOf(string code, RemedyFacts facts) => Capitalise(facts.Resolved ? ResolvedCause(code, facts) : code switch
    {
        FailureCodes.CredentialDisconnected => Disconnected(facts),
        FailureCodes.CredentialNeedsReconnect => NeedsReconnect(facts),
        FailureCodes.ProviderUnauthorized => Unauthorized(facts),
        FailureCodes.GitCredentialRejected => $"The agent finished, but {Provider(facts)} refused {Credential(facts)} when its branch was pushed to {Repository(facts)}, so nothing was published. Reconnect the credential, or link {Repository(facts)} to one that works, then run it again.",
        FailureCodes.ProviderBaseUrlRedirects => Redirects(facts),
        FailureCodes.OAuthExchangeFailed => RenewalUnanswered(facts),
        WorkflowRunRequestRejectionReasons.CredentialRejected => $"{Provider(facts)} refused {Credential(facts)}, so CodeSpace could not check who wrote the pull request on {Repository(facts)}. The author is not in question — the credential is. Reconnect it, or link {Repository(facts)} to one that works, and the next pull request event starts the run.",
        WorkflowRunRequestRejectionReasons.AuthorStandingUnknown => StandingUnknown(facts),
        _ => "Something about a provider connection stopped this from working."
    });

    private static string Disconnected(RemedyFacts facts) => facts.CredentialRetired
        ? $"{Credential(facts)} was retired — its owner left the team or its connection was removed — so it cannot be reconnected. {Repository(facts)} has to be linked to a credential that works before anything can run through it."
        : $"{Credential(facts)} was disconnected, so {Repository(facts)} cannot authenticate with {Provider(facts)}. Reconnect it to put it back, or link {Repository(facts)} to a credential that works.";

    private static string NeedsReconnect(RemedyFacts facts) => facts.CredentialIsStaticToken
        ? $"{Provider(facts)} rejected the token behind {Credential(facts)} — it was revoked or has expired — so {Repository(facts)} cannot authenticate. Paste a new token into it and nothing bound through it has to change."
        : $"{Provider(facts)} refused to renew the sign-in behind {Credential(facts)} — it was revoked, or the app's access was withdrawn — so {Repository(facts)} cannot authenticate. Reconnect it and nothing bound through it has to change.";

    private static string Unauthorized(RemedyFacts facts)
    {
        var status = facts.ProviderStatus is { } code ? $" (HTTP {code})" : "";
        var what = facts.CredentialIsStaticToken ? "The token may have been revoked, may have expired, or may have lost the access it needs." : "The sign-in may have been revoked, or may have lost the access it needs.";

        return $"{Provider(facts)} rejected {Credential(facts)} when it was used for {Repository(facts)}{status}. {what} Replace or reconnect it, or link {Repository(facts)} to a credential that works.";
    }

    private static string Redirects(RemedyFacts facts)
    {
        var from = facts.RequestedOrigin ?? facts.InstanceName ?? "the provider";
        var to = facts.RedirectsTo ?? "another address";
        var notFollowed = "A connected token is only ever sent to the address it was connected for, so the redirect was not followed.";

        return facts.RedirectFix switch
        {
            RedirectRepair.HostSide => $"{Provider(facts)} answered a redirect from {from} that was not followed: the request changes something, or the redirects do not end, and a request is never sent again blindly. The address CodeSpace has is not what is wrong — remove the redirect on the host, or ask whoever runs it.",
            RedirectRepair.DisconnectFirst => $"Requests to {from} are redirected to {to}, which is another host or port. {notFollowed} The provider's address cannot be changed to {to} while {Plural(facts.ConnectedCredentials, "credential")} {(facts.ConnectedCredentials == 1 ? "is" : "are")} connected through it, because a connected token never moves to another host. Disconnect them first and then change the address, or add {to} as a provider of its own and link the repositories to a credential there, or remove the redirect on the host.",
            _ => $"Requests to {from} are redirected to {to}. {notFollowed} Change the provider's address to {to}, or remove the redirect on the host."
        };
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>A renewal that failed under a run is never a final refusal (that is <c>credential_needs_reconnect</c>): the provider was down, or answered something that is not OAuth. It says nothing about the grant, so the sentence does not blame the credential.</summary>
    private static string RenewalUnanswered(RemedyFacts facts)
    {
        var answer = facts.OAuthError is { Length: > 0 } error ? $" (it answered {error})" : "";

        return $"{Provider(facts)} did not renew the sign-in behind {Credential(facts)} when {Repository(facts)} needed it{answer}. That says nothing about the credential itself — a refusal of the grant would have said so — so there is nothing to repair yet. Run it again; if it keeps failing, reconnect it.";
    }

    private static string StandingTitle(RemedyFacts facts) => facts.Lookup switch
    {
        StandingLookupCauses.NoAnswer => $"{Provider(facts)} could not be asked who wrote the pull request",
        StandingLookupCauses.NoCredential => $"There is no credential to ask {Provider(facts)} who wrote the pull request with",
        _ => $"{Provider(facts)} would not say who wrote the pull request"
    };

    /// <summary>What the lookup that failed was, when the refusal says: the credential may not read members (reconnect or replace it), the provider did not answer (nothing to repair), the repository has no credential (link one). A row that does not say words it as either.</summary>
    private static string StandingUnknown(RemedyFacts facts)
    {
        var status = facts.ProviderStatus is { } code ? $" (HTTP {code})" : "";
        var notTheFix = "Setting “Pull requests from” to Anyone is not the fix: nothing was learned about the author.";

        return facts.Lookup switch
        {
            StandingLookupCauses.AccessRefused => $"{Provider(facts)} would not answer who wrote the pull request{status}. {Credential(facts)} lacks the read_api scope or a role on the project to read its members — reconnect or replace it with one that has both. {notTheFix}",
            StandingLookupCauses.NoAnswer => $"{Provider(facts)} could not be asked who wrote the pull request on {Repository(facts)}: it did not answer in time, or answered with an error{status}. That is not a verdict on {Credential(facts)}, so there is nothing to repair; the next pull request event asks again. {notTheFix}",
            StandingLookupCauses.NoCredential => $"No credential is linked to {Repository(facts)}, so {Provider(facts)} was not asked who wrote the pull request. Link it to one that has the read_api scope and a role on the project, and the next pull request event asks again. {notTheFix}",
            _ => $"{Provider(facts)} would not answer who wrote the pull request{status}. Usually {Credential(facts)} lacks the read_api scope or a role on the project to read its members; it can also be a timeout or an error on {Provider(facts)}'s side that clears by itself. The newest refused delivery below says which. If it keeps happening, reconnect or replace the credential with one that has both. {notTheFix}"
        };
    }

    private static string ResolvedCause(string code, RemedyFacts facts) => code switch
    {
        FailureCodes.ProviderBaseUrlRedirects => "The provider's address no longer redirects. Run it again to pick up where this stopped.",
        WorkflowRunRequestRejectionReasons.CredentialRejected or WorkflowRunRequestRejectionReasons.AuthorStandingUnknown => $"{Credential(facts)} works again, so the next pull request event on {Repository(facts)} starts its run.",
        _ => $"{Credential(facts)} works again, so {Repository(facts)} can authenticate. Run it again to pick up where this stopped."
    };

    /// <summary>The provider as a person says it, or "the provider" when the failure did not say which — capitalised where it starts a sentence.</summary>
    private static string Provider(RemedyFacts facts) => facts.Provider ?? "the provider";

    private static string Capitalise(string sentence) => sentence.Length == 0 ? sentence : char.ToUpperInvariant(sentence[0]) + sentence[1..];

    private static string Credential(RemedyFacts facts) => facts.CredentialName is { Length: > 0 } name ? $"“{name}”" : "the credential";

    private static string Repository(RemedyFacts facts) => facts.Repositories ?? "the repository";
}
