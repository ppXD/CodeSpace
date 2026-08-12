namespace CodeSpace.Messages.Failures;

/// <summary>
/// Implemented by a failure that is REPORTING a status a third party returned, rather than choosing
/// one for our own response.
///
/// <para>The distinction is why this is separate from <see cref="IFailure"/> instead of a nullable
/// property on it: an upstream's 404 is a fact about GitHub, not a decision about our API, and only
/// the handful of failures that proxy someone else's answer have one. Whether we mirror it is the
/// API layer's call, made once for all of them.</para>
/// </summary>
public interface IUpstreamStatus
{
    int UpstreamStatus { get; }
}
