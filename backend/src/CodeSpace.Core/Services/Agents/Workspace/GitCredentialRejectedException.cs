using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// Thrown when a push could not be authenticated and trying again with the same credential cannot help: the remote
/// refused the credential even after it was resolved again immediately before the push (a credential that was only stale
/// is replaced by that second resolve and never reaches this), or the credential was disconnected while the run worked.
/// A <see cref="WorkspaceException"/> carries no such classification, which is why the push retry used to spend its whole
/// budget presenting a refused credential three times — and why the failure could only ever read as "git push failed".
/// The remedy is a credential that works: reconnect the provider, or re-link the repository to an active one.
///
/// <para>Deliberately NOT a <see cref="WorkspaceException"/>: that type is the one the push retry retries, and this one is
/// the retry's stopping signal. The two catch sites that swallow a push failure into a recorded, run-preserving publish
/// error name both. The message is already redacted — the workspace provider redacts every credential it ever held before
/// it throws.</para>
/// </summary>
public sealed class GitCredentialRejectedException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.PreconditionRequired;

    public string Code => FailureCodes.GitCredentialRejected;

    /// <summary>What a person acting on the failure needs to find the subject: the repository the push was for and, when the refusal came from a credential that is gone, that credential — both identifiers, never a token.</summary>
    public IReadOnlyDictionary<string, object?>? Details => _details;

    private readonly Dictionary<string, object?>? _details;

    public GitCredentialRejectedException(string message) : base(message) { }

    public GitCredentialRejectedException(string message, Guid? repositoryId) : base(message) { _details = DetailsOf(repositoryId, inner: null); }

    public GitCredentialRejectedException(string message, Exception inner, Guid? repositoryId = null) : base(message, inner) { _details = DetailsOf(repositoryId, inner); }

    private static Dictionary<string, object?>? DetailsOf(Guid? repositoryId, Exception? inner)
    {
        var details = new Dictionary<string, object?>();

        if (repositoryId is { } repository) details["repositoryId"] = repository;
        if (inner is IFailure { Details: { } innerDetails } && innerDetails.TryGetValue("credentialId", out var credentialId)) details["credentialId"] = credentialId;

        return details.Count == 0 ? null : details;
    }
}
