using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Failures;

/// <summary>
/// Reads the failure a thrown exception DECLARED and, when it is one a person can repair outside the run, turns it into the
/// <see cref="PersistedFailure"/> a run's record keeps. Pure and static for the reason <see cref="FailureClassifier"/> is: it is
/// asked from the engine's node runner, from the agent executor's catch and from the bootstrap failure path, none of which should
/// need a container on a failure path.
///
/// <para>It asks the exception CHAIN, not only the top: a provider call that failed inside a retry, a task or an aggregate reaches
/// the engine wrapped, and the credential that was disconnected is still the reason. The first declared provider failure
/// outward-in wins; a wrapper that declares a different failure of its own is believed over what it wraps, as everywhere the
/// taxonomy is consulted.</para>
/// </summary>
public static class ProviderFailureCapture
{
    /// <summary>
    /// The provider-connection failure <paramref name="thrown"/> carries, or null for anything else — including a declared failure
    /// that is not about a provider connection, which stays the sentence it was. <paramref name="repositoryId"/> is the repository
    /// the failing step was acting on, when the step names one.
    /// </summary>
    public static PersistedFailure? From(Exception? thrown, Guid? repositoryId)
    {
        for (var failure = thrown; failure is not null; failure = failure.InnerException)
        {
            if (failure is AggregateException aggregate && FromAny(aggregate.InnerExceptions, repositoryId) is { } inner) return inner;

            if (failure is IFailure declared && FailureCodes.ProviderConnection.Contains(declared.Code)) return Describe(declared, repositoryId);
        }

        return null;
    }

    /// <summary>
    /// The failure a record already carries, kept only when it is one this capture would have written itself — a provider connection failure
    /// (<see cref="FailureCodes.ProviderConnection"/>). For a node that read a failure back from where it happened (a sub-workflow's child run
    /// recorded it) instead of catching an exception: the same filter, so what is carried onward is never wider than what is captured.
    /// </summary>
    public static PersistedFailure? OfRecord(PersistedFailure? recorded) => recorded is not null && FailureCodes.ProviderConnection.Contains(recorded.Code) ? recorded : null;

    private static PersistedFailure? FromAny(IEnumerable<Exception> candidates, Guid? repositoryId) =>
        candidates.Select(candidate => From(candidate, repositoryId)).FirstOrDefault(found => found is not null);

    /// <summary>The repository the failure names itself wins over the one the step was acting on: it is the more specific fact.</summary>
    private static PersistedFailure Describe(IFailure declared, Guid? repositoryId)
    {
        var details = PersistedFailure.DetailsAsText(declared.Details);
        var own = details is not null && details.TryGetValue(RepositoryIdDetail, out var text) && Guid.TryParse(text, out var id) ? id : (Guid?)null;

        return new PersistedFailure { Code = declared.Code, Details = details, RepositoryId = own ?? repositoryId };
    }

    /// <summary>The key a failure's details name its repository under.</summary>
    public const string RepositoryIdDetail = "repositoryId";
}
