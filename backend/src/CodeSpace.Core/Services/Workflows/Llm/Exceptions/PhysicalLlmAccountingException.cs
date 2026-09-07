using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Workflows.Llm.Exceptions;

/// <summary>Accounting could not safely authorize or observe a provider invocation. Never a retryable HTTP fault.</summary>
public sealed class PhysicalLlmAccountingException(string message, Exception? inner = null) : InvalidOperationException(message, inner), IFailure
{
    public FailureKind Kind => FailureKind.Internal;
    public string Code => FailureCodes.Internal;
}
