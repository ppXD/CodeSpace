namespace CodeSpace.Messages.Constants;

/// <summary>
/// Why a trigger could not learn who wrote a pull request — the structured half of an <c>author_standing_unknown</c> refusal. The
/// refusal row's text says it in words; these say it in a form the repository's remedy can branch on, because a lookup that failed
/// for the credential (it lacks the scope or the role) and one that failed for the provider (a timeout, a 429, a 5xx) read the same
/// in a sentence and must not send the same people to the same buttons. Written beside the refusal under <see cref="DetailKey"/>,
/// next to the status the provider answered, and pinned by test: a rename turns every stored row into "cause unknown".
/// </summary>
public static class StandingLookupCauses
{
    /// <summary>The failure-details key the cause is stored under.</summary>
    public const string DetailKey = "lookup";

    /// <summary>The repository has no credential bound to ask the provider with.</summary>
    public const string NoCredential = "no_credential";

    /// <summary>The provider answered that the credential may not read the project's members (HTTP 403, or a missing scope) — the credential's to fix.</summary>
    public const string AccessRefused = "access_refused";

    /// <summary>The provider did not answer in time, answered with an error (429, 5xx) or the lookup failed some other way — nothing says the credential is wrong, and the next event asks again.</summary>
    public const string NoAnswer = "no_answer";
}
