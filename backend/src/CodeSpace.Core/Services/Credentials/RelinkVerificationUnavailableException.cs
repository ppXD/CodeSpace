using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// Thrown when a relink could not find out whether its target can read a repository, because the provider did not answer
/// in a way that says anything about the target — an outage, a dropped connection, a rate limit. Nothing was learned, so
/// the refusal must not blame the target ("pick a credential that reaches them" is the wrong advice for an outage); the
/// remedy is to try again, and nothing was moved. The provider's own error is the inner exception and is logged, not shown.
/// </summary>
public sealed class RelinkVerificationUnavailableException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Unavailable;

    public string Code => FailureCodes.ProviderError;

    public RelinkVerificationUnavailableException(string targetName, string sourceName, Exception inner)
        : base($"Could not check whether credential '{targetName}' can read the repositories bound through '{sourceName}': the provider did not answer. Nothing was moved. Try again in a moment.", inner)
    {
    }
}
