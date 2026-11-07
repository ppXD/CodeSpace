using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Agents.Exceptions;

/// <summary>
/// A call reached an agent run that has already ended — succeeded, failed, been held for review, stopped or recovered —
/// as a NEW tool call to claim, or as a decision claimed before the end that was about to be parked after it. Nothing
/// reads its result any more, so it is refused before anything is recorded or shown to a human; the MCP handler answers
/// it as final, never as something to retry.
/// </summary>
public sealed class AgentRunEndedException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Conflict;
    public string Code => FailureCodes.AgentRunEnded;
    public AgentRunEndedException(Guid runId) : base($"Agent run {runId} has already ended; it takes no new tool call.") { }
}
