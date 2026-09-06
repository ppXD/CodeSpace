namespace CodeSpace.Messages.Contracts;

/// <summary>
/// ONE upstream stage a run's own REPOSITORY POLICY puts out of reach — read "not applicable", never "missing".
/// The distinction is the whole point: a Required stage with no evidence means the Success claim skipped work it
/// owed and must park; a stage the policy forbids the run from ever exercising is owed by nobody, and parking on
/// it strands the run forever because no answer from inside it can change the policy.
///
/// <para>Distinct from <see cref="StageRequiredness"/>'s two authorized-NA variants, deliberately: those are
/// AUTHORED per MODE and hold for every run in it. This one is DERIVED per RUN from that run's own durable
/// evidence, so a mode whose runs normally owe the stage still owes it everywhere the policy permits it.</para>
/// </summary>
public sealed record UpstreamStageNotApplicable
{
    /// <summary>The stage the policy put out of reach.</summary>
    public required CompletionStage Stage { get; init; }

    /// <summary>The backend-authored clause naming WHY, plus what the run delivered instead — rendered verbatim by the stop recital and the Room, so the two can never word the same fact differently.</summary>
    public required string Reason { get; init; }
}
