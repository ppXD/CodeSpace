using CodeSpace.Core.Services.Sessions;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions;

/// <summary>
/// Unit-proves the PURE "which attempt wins" chooser (<see cref="SessionTurnAttempts"/>) — the shared choke point every
/// rerun-aware session read defers to, so a turn's display/continuity/context never reads a superseded attempt while a
/// later rerun in the same lineage actually succeeded.
/// </summary>
[Trait("Category", "Unit")]
public class SessionTurnAttemptsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 28, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_reruns_the_single_attempt_wins()
    {
        var id = Guid.NewGuid();
        var rows = new[] { new SessionTurnAttempts.AttemptRow(id, WorkflowRunStatus.Success, T0) };

        SessionTurnAttempts.ResolveEffectiveId(rows).ShouldBe(id);
    }

    [Fact]
    public void An_original_failure_with_a_succeeded_rerun_the_rerun_wins()
    {
        var original = new SessionTurnAttempts.AttemptRow(Guid.NewGuid(), WorkflowRunStatus.Failure, T0);
        var rerun = new SessionTurnAttempts.AttemptRow(Guid.NewGuid(), WorkflowRunStatus.Success, T0.AddMinutes(1));

        SessionTurnAttempts.ResolveEffectiveId(new[] { original, rerun }).ShouldBe(rerun.Id,
            "a rerun exists precisely to fix a failed attempt — its success is the turn's real outcome");
    }

    [Fact]
    public void A_multi_level_rerun_chain_the_lineage_terminal_success_wins_even_if_not_newest()
    {
        var original = new SessionTurnAttempts.AttemptRow(Guid.NewGuid(), WorkflowRunStatus.Failure, T0);
        var succeeded = new SessionTurnAttempts.AttemptRow(Guid.NewGuid(), WorkflowRunStatus.Success, T0.AddMinutes(1));
        var laterReplayFailure = new SessionTurnAttempts.AttemptRow(Guid.NewGuid(), WorkflowRunStatus.Failure, T0.AddMinutes(2));

        SessionTurnAttempts.ResolveEffectiveId(new[] { original, succeeded, laterReplayFailure }).ShouldBe(succeeded.Id,
            "the newest SUCCEEDED attempt wins over an even-newer failed one — a turn's real outcome is never masked by a later failure");
    }

    [Fact]
    public void All_attempts_failed_the_newest_wins_never_null_or_empty()
    {
        var older = new SessionTurnAttempts.AttemptRow(Guid.NewGuid(), WorkflowRunStatus.Failure, T0);
        var newer = new SessionTurnAttempts.AttemptRow(Guid.NewGuid(), WorkflowRunStatus.Failure, T0.AddMinutes(1));

        SessionTurnAttempts.ResolveEffectiveId(new[] { older, newer }).ShouldBe(newer.Id,
            "no attempt succeeded — fall back to the newest so the turn is never silently invisible");
    }

    [Fact]
    public void Two_succeeded_attempts_the_newest_succeeded_one_wins()
    {
        var firstSuccess = new SessionTurnAttempts.AttemptRow(Guid.NewGuid(), WorkflowRunStatus.Success, T0);
        var secondSuccess = new SessionTurnAttempts.AttemptRow(Guid.NewGuid(), WorkflowRunStatus.Success, T0.AddMinutes(1));

        SessionTurnAttempts.ResolveEffectiveId(new[] { firstSuccess, secondSuccess }).ShouldBe(secondSuccess.Id);
    }
}
