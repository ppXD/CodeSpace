using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// The ONE place a real-model arm buys a repair for a gateway FORMAT fault, and the ONE place the bound on that
/// repair lives.
///
/// <para><b>Why an arm needs this at all.</b> The operator's gateway raises
/// <c>API Error: Content block is not a thinking block</c> mid-stream and kills the agent's tail. Production already
/// owns the repair — <see cref="AgentRetryCauses.ApplyFormatFaultMitigation"/>, invoked by the supervisor's respawn,
/// <c>AgentCodeNode</c>'s retry and the benchmark runner. A real-model E2E arm has no retry path of its own: it drives
/// the executor/harness itself, <see cref="RealModelRunClassifier.IsGatewayInfra"/> (correctly) calls the fault infra,
/// and the arm throws <see cref="AgentExecutionInfraException"/> → a non-gating skip with ZERO repair attempts. Nine
/// consecutive main runs measured NOTHING that way. The gateway fault is the operator's; the LOST MEASUREMENT was
/// ours.</para>
///
/// <para><b>The bound, and why it is here rather than at each throw site.</b> The repair is bought EXACTLY ONCE —
/// the same bound <c>AgentCodeNode</c> encodes for the production respawn: an attempt that already ran repaired and
/// died of the SAME fault has proven the repair does not hold, so a second identical re-dispatch would only re-bill a
/// broken gateway. Encoded here as the ABSENCE of a loop (<see cref="AttemptTwiceAsync"/> awaits <c>first</c>, then at
/// most <c>repaired</c>, then rethrows), so no arm can drift into two repairs by editing its own call site.</para>
///
/// <para><b>FORMAT faults only.</b> <see cref="IsRepairable"/> demands both that the fault already be on today's
/// non-gating skip path (<c>RealModelGate.IsGatewayInfraFailure</c>) and that production's own closed marker
/// vocabulary name it a <see cref="AgentRetryCauses.GatewayFormatFault"/>. Every other infra class — a 429, an
/// HTTP 500, a dropped connection, a deadline bust — propagates on the FIRST attempt exactly as before: no repair
/// exists for those, and re-dispatching them would only spend the owner's tokens on the same weather.</para>
///
/// <para><b>Cost of the repair, stated in the verdict.</b> A repaired measurement was produced with extended thinking
/// DISABLED — <c>BenchmarkScorecard.TallyFormatFaults</c> already names that "a materially different configuration".
/// So the attempt that produced the measurement is stamped INTO the note the gate reports, and a lane reader can tell
/// a first-attempt pass from a repaired one without opening the log.</para>
///
/// <para><b>Timing.</b> Both attempts run inside ONE of the calling gate's per-attempt deadlines. That fits because
/// the fault is PRE-TURN: the CLI dies in seconds, before the model gets a turn (production's own diagnosis), so the
/// repaired attempt still has effectively the whole deadline. A hypothetical LATE format fault is still bounded — the
/// gate's own per-attempt deadline wraps the pair and records a non-converging miss rather than running unbounded.</para>
/// </summary>
public static class RealModelFormatFaultRepair
{
    /// <summary>The unrepaired dispatch plus AT MOST one repaired re-dispatch. The whole bound, and the number every stamp below counts against.</summary>
    public const int MaxAttempts = 2;

    /// <summary>What <see cref="WithMitigatedRetryAsync"/> buys — named in the verdict so a repaired measurement announces the configuration it was produced under.</summary>
    public const string MitigatedRepair = "a fresh conversation with " + AgentRetryCauses.MaxThinkingTokensEnvVar + "=0 (AgentRetryCauses.ApplyFormatFaultMitigation)";

    /// <summary>What <see cref="WithColdRestageAsync"/> buys — for an arm whose SUBJECT is the restored conversation, which the mitigation would DROP (it would then measure something else).</summary>
    public const string ColdRestageRepair = "a COLD re-stage of the whole fixture (fresh run, fresh transcript, then resume)";

    /// <summary>How much of a fault's own text a stamp carries — enough to name the gateway's message, bounded so a stderr tail cannot swallow the verdict line.</summary>
    private const int ExcerptLimit = 400;

    /// <summary>Attempt 1's task transform: the task EXACTLY as the arm authored it. Named rather than inlined so the two attempts read as one pair at the call site.</summary>
    private static AgentTask Unrepaired(AgentTask task) => task;

    /// <summary>For an arm that AUTHORS the faulting run's task: re-dispatch once through production's own mitigation. <paramref name="driveOnce"/> is invoked with the transform to fold onto its task, and MUST stage itself fresh per call.</summary>
    public static Task<(RealModelOutcome Outcome, string Note)> WithMitigatedRetryAsync(Func<Func<AgentTask, AgentTask>, Task<(RealModelOutcome Outcome, string Note)>> driveOnce) =>
        AttemptTwiceAsync(() => driveOnce(Unrepaired), () => driveOnce(AgentRetryCauses.ApplyFormatFaultMitigation), MitigatedRepair);

    /// <summary>For an arm whose SUBJECT the mitigation would destroy: re-drive the identical COLD staging once. <paramref name="stageColdAndDrive"/> MUST stage its whole fixture fresh per call — a stale transcript cannot satisfy a retry.</summary>
    public static Task<(RealModelOutcome Outcome, string Note)> WithColdRestageAsync(Func<Task<(RealModelOutcome Outcome, string Note)>> stageColdAndDrive) =>
        AttemptTwiceAsync(stageColdAndDrive, stageColdAndDrive, ColdRestageRepair);

    /// <summary>The bound itself: attempt <paramref name="first"/>, and on a FORMAT fault attempt <paramref name="repaired"/> once. No loop, so the repair can never be bought twice. Every non-format fault propagates from the first attempt untouched.</summary>
    internal static async Task<(RealModelOutcome Outcome, string Note)> AttemptTwiceAsync(Func<Task<(RealModelOutcome Outcome, string Note)>> first, Func<Task<(RealModelOutcome Outcome, string Note)>> repaired, string repair)
    {
        try
        {
            var measured = await first().ConfigureAwait(false);

            return (measured.Outcome, measured.Note + FirstAttemptStamp);
        }
        catch (Exception fault) when (IsRepairable(fault))
        {
            return await RepairOnceAsync(repaired, repair, fault).ConfigureAwait(false);
        }
    }

    /// <summary>The one repaired re-dispatch. A repeat of the SAME fault is terminal — rethrown as the infra exception the arm would have thrown anyway, so the arm still SKIPS (never passes) and the reason names both attempts.</summary>
    private static async Task<(RealModelOutcome Outcome, string Note)> RepairOnceAsync(Func<Task<(RealModelOutcome Outcome, string Note)>> repaired, string repair, Exception fault)
    {
        try
        {
            var measured = await repaired().ConfigureAwait(false);

            return (measured.Outcome, measured.Note + RepairedAttemptStamp(repair, fault));
        }
        catch (Exception again) when (IsRepairable(again))
        {
            throw new AgentExecutionInfraException(BothAttemptsFaultedReason(repair, fault, again));
        }
    }

    /// <summary>
    /// Whether a fault is the ONE shape a repair exists for. Both halves are load-bearing. The
    /// <c>IsGatewayInfraFailure</c> half keeps this strictly inside today's non-gating skip path — a genuine
    /// assertion failure whose text happens to quote the gateway's message must still RED, never be re-driven into a
    /// skip. The <see cref="AgentRetryCauses.Classify"/> half reads production's own closed marker vocabulary rather
    /// than keeping a second copy, so the arms, the run classifier and the production retry lane can never disagree
    /// about what "the gateway mangled the wire" means. Read off <see cref="Exception.ToString"/> so a fault wrapped
    /// in an aggregate or an inner exception still classifies.
    /// </summary>
    internal static bool IsRepairable(Exception fault) =>
        RealModelGate.IsGatewayInfraFailure(fault) && AgentRetryCauses.Classify(fault.ToString()) == AgentRetryCauses.GatewayFormatFault;

    /// <summary>The stamp on a measurement the FIRST dispatch produced — the unmodified configuration the arm declares.</summary>
    internal static readonly string FirstAttemptStamp = $" [measured on attempt 1/{MaxAttempts}: the arm's own configuration, no gateway format-fault repair needed]";

    /// <summary>The stamp on a measurement the REPAIRED dispatch produced — names the repair and the fault it bought its way past, so a repaired pass is never silently read as a clean one.</summary>
    internal static string RepairedAttemptStamp(string repair, Exception fault) =>
        $" [measured on attempt {MaxAttempts}/{MaxAttempts}: AFTER the one repair this arm buys — {repair}. Attempt 1/{MaxAttempts} hit a gateway format fault: {Excerpt(fault)}]";

    /// <summary>The skip reason when the repair did NOT hold: both attempts named, so a lane reader sees that a repair was attempted and still measured nothing.</summary>
    internal static string BothAttemptsFaultedReason(string repair, Exception first, Exception second) =>
        $"a gateway format fault on BOTH of the {MaxAttempts} attempts this arm buys — the one repair ({repair}) did not hold, so NOTHING was measured (a skip, never a pass). "
      + $"attempt 1/{MaxAttempts}: {Excerpt(first)}; attempt {MaxAttempts}/{MaxAttempts} (repaired): {Excerpt(second)}";

    /// <summary>A fault's own message, bounded — the gateway's text is the diagnostic, a folded stderr tail is not.</summary>
    private static string Excerpt(Exception fault) =>
        fault.Message.Length <= ExcerptLimit ? fault.Message : fault.Message[..ExcerptLimit] + "…";
}
