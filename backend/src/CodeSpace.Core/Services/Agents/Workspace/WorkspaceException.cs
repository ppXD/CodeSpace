using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// Thrown when a workspace can't be prepared — a failed clone, a missing ref, or git being unavailable.
/// The executor maps it to a clean agent-run failure (the run never starts the harness).
///
/// <para>Not sealed so a failure with a known cause can say so (<see cref="GitCredentialHelperUnsupportedException"/>) while
/// every catch site that handles this type keeps handling it.</para>
/// </summary>
public class WorkspaceException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Unprocessable;

    /// <summary><c>provider_base_url_redirects</c> when git was redirected to another origin (<see cref="GitRemoteRedirect"/>), else <c>workspace_unresolvable</c>.</summary>
    public string Code => Redirect?.Code ?? FailureCodes.WorkspaceUnresolvable;

    /// <summary>The redirect's own details (the origin to edit the base URL to) when git was redirected; none otherwise.</summary>
    public IReadOnlyDictionary<string, object?>? Details => Redirect?.Details;

    private ProviderBaseUrlRedirectsException? Redirect => InnerException as ProviderBaseUrlRedirectsException;

    public WorkspaceException(string message) : base(message) { }

    public WorkspaceException(string message, Exception inner) : base(message, inner) { }

    /// <summary><paramref name="message"/>, followed by what the redirect <paramref name="redirect"/> says to do about it.</summary>
    public WorkspaceException(string message, ProviderBaseUrlRedirectsException redirect) : base($"{message} — {redirect.Message}", redirect) { }
}
