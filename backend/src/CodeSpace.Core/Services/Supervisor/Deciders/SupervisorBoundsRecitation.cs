using System.Text;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor.Deciders;

/// <summary>
/// P5-3 (C3 absorption — the loop bounds turn VISIBLE): renders the run's two silent kill-counters — the
/// no-progress streak and the total-spawn count — so the decider sees how close it is to a force-stop BEFORE
/// burning another evidence-less turn. Both bounds existed and killed runs (<c>SupervisorBounds</c>); the model
/// just never SAW them — it could only infer the streak from a post-hoc tier-escalation note, which is exactly
/// the blindness behind the observed spawn/retry loops that march a run into its no-progress kill. Pure + pinned,
/// mirroring <see cref="SupervisorBudgetRecitation"/> (the cost member of the same bounds family). Renders null
/// while both counters are zero — a healthy young run pays no prompt tax and its prompt stays byte-identical.
/// </summary>
public static class SupervisorBoundsRecitation
{
    /// <summary>The block's pinned header — a stable prompt landmark (tests + the model key on it), mirroring <see cref="SupervisorBudgetRecitation.Header"/>.</summary>
    public const string Header = "RUN BOUNDS (recite before deciding — hitting a bound force-stops the run):";

    /// <summary>Render the bounds block for the decider's prompt, or null when every counter is zero (nothing is at risk yet).</summary>
    public static string? Render(int noProgressDecisions, int maxNoProgressDecisions, int totalSpawnedAgents, int? maxTotalSpawns, int resolveAttempts = 0, int? maxResolveAttempts = null)
        => Render(new SupervisorTurnContext
        {
            NoProgressDecisions = noProgressDecisions,
            MaxNoProgressDecisions = maxNoProgressDecisions,
            TotalSpawnedAgents = totalSpawnedAgents,
            MaxTotalSpawns = maxTotalSpawns,
            MaxResolveAttempts = maxResolveAttempts,
        }, resolveAttempts, hasCorrectionTurn: false);

    /// <summary>Render from the real durable turn context, including the single correction runway when the runtime admits it.</summary>
    public static string? Render(SupervisorTurnContext context) => Render(context, context.PriorDecisions.Count(d => d.DecisionKind == SupervisorDecisionKinds.Resolve), SupervisorBounds.HasReauthorableCorrectionTurn(context));

    private static string? Render(SupervisorTurnContext context, int resolveAttempts, bool hasCorrectionTurn)
    {
        if (context.NoProgressDecisions <= 0 && context.TotalSpawnedAgents <= 0 && resolveAttempts <= 0) return null;

        var builder = new StringBuilder(Header);

        if (context.NoProgressDecisions > 0)
        {
            var left = Math.Max(0, context.MaxNoProgressDecisions - context.NoProgressDecisions);
            var runway = hasCorrectionTurn
                ? "ONE correction turn is available because the server rejected the last action; fix the rejected action now, because a repeated rejection or any other evidence-less decision force-stops this run"
                : left == 1 ? "ONE more evidence-less decision force-stops this run" : $"{left} more evidence-less decisions force-stop this run";

            builder.AppendLine().Append($"- no-progress decisions: {context.NoProgressDecisions} of {context.MaxNoProgressDecisions} — {runway}. A decision counts as progress only when it lands SETTLED EVIDENCE: an objectively accepted unit, a merge that integrates new work, or an answered human ask. Prefer the action most likely to produce verifiable evidence; if none can, stop honestly or ask a human now instead of burning the remaining decisions.");
        }

        if (context.TotalSpawnedAgents > 0)
            builder.AppendLine().Append($"- agents spawned: {context.TotalSpawnedAgents} of {context.MaxTotalSpawns ?? SupervisorLane.DefaultMaxTotalSpawns} total-spawn cap (every spawn fan-out member and every retry counts one; a wave that would exceed the cap is refused).");

        // P5-5: the resolver runway — a resolve PAST the cap doesn't get refused, it force-stops the whole run
        // (ResolveAttemptsExceeded), so the model must know the count before spending the run's life on one more.
        if (resolveAttempts > 0)
            builder.AppendLine().Append($"- resolve attempts: {resolveAttempts} of {context.MaxResolveAttempts ?? SupervisorLane.DefaultMaxResolveAttempts} resolve cap — a resolve past the cap force-stops this run. If the reconciliation still is not VERIFIED within the cap, stop and leave the conflict to a human.");

        return builder.ToString();
    }
}
