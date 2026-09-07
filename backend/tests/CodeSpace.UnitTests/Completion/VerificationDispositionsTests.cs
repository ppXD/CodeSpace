using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Contracts;
using Shouldly;

namespace CodeSpace.UnitTests.Completion;

/// <summary>
/// 5.6 residual — <see cref="VerificationDispositions.Classify"/> is the completion reducer's ONLY authority on the
/// F0 Verification dimension, and it takes exactly two signals: the objective ACCEPTANCE oracle's verdict and detail.
/// It has no parameter for an output-review / critic verdict at all — so a run whose configured output review could
/// not run (both the S8 agent reviewer and the model-critic fallback exhausted) can never be classified
/// <see cref="VerificationDisposition.Passed"/> by way of that review, regardless of what <c>AgentRunResult.UnreviewedReason</c>
/// or <c>ReviewFeedback</c> say. This pins that separation directly at its source so it can never be widened by a
/// future rider onto the reducer.
/// </summary>
[Trait("Category", "Unit")]
public sealed class VerificationDispositionsTests
{
    [Fact]
    public void An_unreviewed_and_ungraded_run_classifies_Unknown_never_Passed()
    {
        // The exact facts a run whose output review exhausted both rungs presents to this reducer: no acceptance
        // oracle ever ran, because the task carried none. There is no third input this call could read a review from.
        VerificationDispositions.Classify(acceptancePassed: null, detail: null, workPresent: true)
            .ShouldBe(VerificationDisposition.Unknown, "an unreviewed result must read as UNKNOWN, never as an examined-and-passed one");
    }
}
