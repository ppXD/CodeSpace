using CodeSpace.Core.Services.Learning;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>
/// Divides a set of scored runs by the Arc-D lesson A/B arm — the fold that turns "the arm is recorded" into "the
/// arm is measured". The arm has been on every supervisor decision row since Arc D and on
/// <see cref="SupervisorEvalScorecard"/> per run, yet no rollup ever divided a RATE by it, so the whole point of
/// running an A/B — does injecting the team's own distilled lessons move the north-star — had never been answered.
///
/// <para>Pure + DB-free, so it unit-tests exhaustively. Arms are NEVER merged: <see cref="Unmeasured"/> (a run with
/// no decision ledger at all — single-agent, plan-map) is a different claim from <c>none</c> (a run that WAS in the
/// experiment and drew the empty-lesson control). Ordering is fixed so two windows are readable side by side.</para>
/// </summary>
public static class LessonArmSlicer
{
    /// <summary>The bucket for runs that carry no arm — they were never in the experiment, as opposed to <c>LessonArms.None</c>, which is its control group.</summary>
    public const string Unmeasured = "unmeasured";

    /// <summary>Fixed render order: the two experiment arms, then the control, then the runs the experiment never touched.</summary>
    private static readonly string[] ArmOrder = [LessonArms.Injected, LessonArms.Withheld, LessonArms.None, Unmeasured];

    /// <summary>Slice <paramref name="rows"/> by arm — one entry per arm actually PRESENT (an arm with no runs has no slice, never a 0/0 rate), in <see cref="ArmOrder"/>.</summary>
    public static IReadOnlyList<LessonArmSlice> Slice(IReadOnlyList<ArmedRunScore> rows)
    {
        var byArm = rows.GroupBy(Arm, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        return ArmOrder
            .Where(byArm.ContainsKey)
            .Select(arm => Fold(arm, byArm[arm]))
            .Concat(byArm.Keys.Where(arm => !ArmOrder.Contains(arm, StringComparer.Ordinal)).OrderBy(arm => arm, StringComparer.Ordinal).Select(arm => Fold(arm, byArm[arm])))
            .ToList();
    }

    /// <summary>A row's arm bucket — a blank/absent arm is <see cref="Unmeasured"/>, never silently folded into the <c>none</c> control.</summary>
    private static string Arm(ArmedRunScore row) => string.IsNullOrWhiteSpace(row.LessonArm) ? Unmeasured : row.LessonArm;

    private static LessonArmSlice Fold(string arm, IReadOnlyList<ArmedRunScore> rows) => new()
    {
        Arm = arm,
        Runs = rows.Count,
        SolvedRuns = rows.Count(r => r.Solved),
        DeliveredRuns = rows.Count(r => r.Delivered),
        UnattendedSolvedWithDeliveryRuns = rows.Count(r => r.UnattendedSolvedWithDelivery),
        UnattendedSolveWithDeliveryRate = (double)rows.Count(r => r.UnattendedSolvedWithDelivery) / rows.Count,
    };
}
