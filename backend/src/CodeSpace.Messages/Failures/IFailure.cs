namespace CodeSpace.Messages.Failures;

/// <summary>
/// Implemented by an exception that knows what it means.
///
/// <para>An interface rather than a base class on purpose: all 35 domain exceptions here already
/// derive straight from <see cref="Exception"/>, and rebasing them would be a breaking change to
/// every catch site for no gain. Adoption is one interface at a time, and
/// <c>DomainExceptionConventionTests</c> counts what has not adopted yet so the remainder cannot
/// quietly become permanent.</para>
///
/// <para>What it deliberately does NOT carry: an HTTP status. That belongs to the one consumer that
/// speaks HTTP — see <c>FailureKind</c>.</para>
/// </summary>
public interface IFailure
{
    FailureKind Kind { get; }

    /// <summary>
    /// The stable machine-readable name for this failure, from <see cref="FailureCodes"/>. Clients
    /// branch on it, so it is a wire contract: renaming one is a breaking change, never a refactor.
    /// </summary>
    string Code { get; }

    /// <summary>
    /// Extra fields a caller needs in order to ACT on the failure — the provider to link, the scopes
    /// to grant, the validation errors to show. Merged into the response body as-is.
    ///
    /// <para>Only for things the caller must act on. Diagnostic context belongs in the log, where it
    /// can carry detail without becoming a public contract.</para>
    /// </summary>
    IReadOnlyDictionary<string, object?>? Details => null;

    /// <summary>
    /// What the caller may be told, when that must differ from <see cref="Exception.Message"/>.
    ///
    /// <para>Null means the message is safe to pass through. Override it wherever the diagnostic
    /// message names something the caller has not earned — a tenancy refusal says which user and
    /// which team, and repeating that back would confirm a team id to someone who guessed it.</para>
    /// </summary>
    string? ClientMessage => null;
}
