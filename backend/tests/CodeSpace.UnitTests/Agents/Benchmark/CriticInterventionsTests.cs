using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents.Benchmark;

/// <summary>
/// 🟢 Unit: the critic A/B's intervention decomposition. A result held because its review could not examine it
/// (<c>output-unreviewed</c>) used to fall through every class: never a flag, so a broken one counted as
/// "critic-missed" and a correct one — the fail-closed rule's burden — was counted nowhere. It is now its own class,
/// split by the objective grade, and the classes partition the arm.
/// </summary>
[Trait("Category", "Unit")]
public sealed class CriticInterventionsTests
{
    [Fact]
    public void Each_result_lands_in_the_class_its_exit_and_grade_name()
    {
        var results = new[]
        {
            Result("completed", passed: true, reviseRounds: 1),                                            // flagged → revised → shipped correct
            Result(CriticInterventions.FlaggedExitReason, passed: false),                                    // held broken
            Result(CriticInterventions.FlaggedExitReason, passed: true),                                     // held correct
            Result(AgentRunExecutor.OutputUnreviewedExitReason, passed: false),                              // unreviewed, broken
            Result(AgentRunExecutor.OutputUnreviewedExitReason, passed: true),                               // unreviewed, correct
            Result(AgentRunExecutor.OutputUnreviewedExitReason, passed: true, reviseRounds: 1),              // revised, then unreviewed: still a hold, not a resolve
            Result("completed", passed: false),                                                              // silent on broken: the miss
            Result("completed", passed: true),                                                               // silent on correct: no intervention
        };

        CriticInterventions.Decompose(results).ShouldBe(new CriticInterventionTally
        {
            CatchAndResolve = 1,
            TrueHold = 1,
            FalseHold = 1,
            TrueUnreviewedHold = 1,
            FalseUnreviewedHold = 2,
            Missed = 1,
        });
    }

    [Fact]
    public void An_unreviewed_broken_change_is_never_a_miss()
    {
        CriticInterventions.Decompose(new[] { Result(AgentRunExecutor.OutputUnreviewedExitReason, passed: false) }).Missed.ShouldBe(0, "it was held for a human, so nothing broken shipped");
    }

    private static BenchmarkResult Result(string exitReason, bool passed, int reviseRounds = 0) => new()
    {
        TaskId = Guid.NewGuid().ToString("N"),
        Mode = BenchmarkMode.HarnessCli,
        RunStatus = passed ? AgentRunStatus.Succeeded : AgentRunStatus.NeedsReview,
        Grade = new BenchmarkGrade { Passed = passed, Detail = passed ? "passed" : "failed" },
        ExitReason = exitReason,
        ReviseRounds = reviseRounds,
        McpFullCatalog = false,
    };
}
