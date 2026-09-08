using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark.Exceptions;

/// <summary>A paired qualification cell could not append its immutable evidence, so no auditable claim can be produced.</summary>
public sealed class DurableBenchmarkObservationException : InvalidOperationException, IFailure
{
    public DurableBenchmarkObservationException(string message, Exception innerException) : base(message, innerException) { }

    public FailureKind Kind => FailureKind.Internal;
    public string Code => FailureCodes.Internal;
}
