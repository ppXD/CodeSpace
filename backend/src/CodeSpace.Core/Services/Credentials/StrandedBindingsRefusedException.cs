using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// The repair sweep found a successor for a dead credential and none of what is bound through it could move there: every repository was
/// refused (the successor's connection already holds the project, or its scopes cannot register the connection's hook). A refusal of that kind does
/// not heal by being asked again, so the sweep treats it as a failure of the candidate — logged with the reasons, and set aside after a few passes
/// in a row — instead of "repairing" it every tick for ever and letting it hold a place in the pass that a repairable credential could have had.
/// Nothing was written.
/// </summary>
internal sealed class StrandedBindingsRefusedException : Exception, IFailure
{
    /// <summary>Well-formed, and blocked by a rule the sweep cannot change: the person who connected the successor is the one who can (unbind the duplicate, grant the scope).</summary>
    public FailureKind Kind => FailureKind.Unprocessable;

    public string Code => FailureCodes.InvalidRequest;

    public StrandedBindingsRefusedException(Guid deadCredentialId, Guid successorId, IReadOnlyList<string> reasons)
        : base($"None of what is bound through credential {deadCredentialId} can move onto credential {successorId}: {string.Join(" ", reasons.Take(3))}")
    {
    }
}
