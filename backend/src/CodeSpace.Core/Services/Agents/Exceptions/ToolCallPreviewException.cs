using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Agents.Exceptions;

/// <summary>
/// A tool call could not be shown to a human for approval as it would run — the pull request it names could not be read,
/// or its head is not the commit the call names — so it is answered to the model instead of parked. The message is
/// model-facing and is redacted at the MCP handler's choke point like every tool result.
/// </summary>
public sealed class ToolCallPreviewException(string message) : Exception(message), IFailure
{
    public FailureKind Kind => FailureKind.Unprocessable;
    public string Code => FailureCodes.ToolCallNotPreviewable;
}
