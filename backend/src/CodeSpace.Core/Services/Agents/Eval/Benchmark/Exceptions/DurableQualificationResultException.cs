using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark.Exceptions;

/// <summary>The immutable paired result could not be bound to its complete durable evidence census, so no auditable claim can be produced.</summary>
public sealed class DurableQualificationResultException : InvalidOperationException, IFailure
{
    public DurableQualificationResultException(string message) : base(message) { }

    public FailureKind Kind => FailureKind.Internal;
    public string Code => FailureCodes.Internal;
}
