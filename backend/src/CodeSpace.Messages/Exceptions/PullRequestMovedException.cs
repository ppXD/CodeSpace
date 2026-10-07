using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// A write pinned to what a reviewer saw of a pull request — its head commit, its base branch — found the pull request
/// moved when it was read again just before the write, so the write was not sent. Thrown by the pull-request service
/// before a merge or a review; the nodes turn it into a failure that says nothing was merged or submitted.
/// </summary>
public sealed class PullRequestMovedException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Conflict;

    public string Code => FailureCodes.PullRequestMoved;

    public IReadOnlyDictionary<string, object?>? Details => new Dictionary<string, object?> { ["number"] = Number, ["pinned"] = Pinned, ["expected"] = Expected, ["actual"] = Actual };

    public PullRequestMovedException(int number, string pinned, string expected, string actual)
        : base($"its {pinned} is now {actual}, not {expected}, the one it was pinned to")
    {
        Number = number;
        Pinned = pinned;
        Expected = expected;
        Actual = actual;
    }

    /// <summary>The pull request's number.</summary>
    public int Number { get; }

    /// <summary>What moved: <c>head</c> or <c>base</c>.</summary>
    public string Pinned { get; }

    /// <summary>What it was pinned to.</summary>
    public string Expected { get; }

    /// <summary>What it is now.</summary>
    public string Actual { get; }
}
