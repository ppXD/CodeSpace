using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>
/// The intervention decomposition a critic A/B reports for its critic-on arm, over each result's terminal exit reason
/// and objective grade (<see cref="CriticInterventionTally"/>). Pure, so the classification is pinned without a live
/// corpus pass. A result held because its review could not examine it (<see cref="AgentRunExecutor.OutputUnreviewedExitReason"/>)
/// is its own class: counted as a flag it would credit the critic with a judgement it never made, and counted as
/// silence it would report a held broken change as a miss.
/// </summary>
public static class CriticInterventions
{
    /// <summary>The exit reason of a result the critic flagged and the revise budget did not clear.</summary>
    public const string FlaggedExitReason = "output-flagged";

    public static CriticInterventionTally Decompose(IReadOnlyList<BenchmarkResult> results) => new()
    {
        CatchAndResolve = results.Count(r => Flagged(r) && r.Grade.Passed && !Held(r)),
        TrueHold = results.Count(r => r.ExitReason == FlaggedExitReason && !r.Grade.Passed),
        FalseHold = results.Count(r => r.ExitReason == FlaggedExitReason && r.Grade.Passed),
        TrueUnreviewedHold = results.Count(r => r.ExitReason == AgentRunExecutor.OutputUnreviewedExitReason && !r.Grade.Passed),
        FalseUnreviewedHold = results.Count(r => r.ExitReason == AgentRunExecutor.OutputUnreviewedExitReason && r.Grade.Passed),
        Missed = results.Count(r => !Flagged(r) && !Held(r) && !r.Grade.Passed),
    };

    /// <summary>The critic objected at least once: a revise round ran, or the run ended still flagged.</summary>
    public static bool Flagged(BenchmarkResult result) => result.ReviseRounds > 0 || result.ExitReason == FlaggedExitReason;

    /// <summary>The run ended held for a human by its output review, flagged or unreviewed.</summary>
    private static bool Held(BenchmarkResult result) => result.ExitReason is FlaggedExitReason or AgentRunExecutor.OutputUnreviewedExitReason;
}
