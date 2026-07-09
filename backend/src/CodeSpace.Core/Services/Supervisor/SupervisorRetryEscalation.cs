using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// A2 (P4-2) — tier escalation on retry: when a retry follows run-EVIDENCE that the prior attempt's model was
/// insufficient for this unit (a self-report/acceptance-grade CONTRADICTION — <see cref="Agents.AgentContradiction"/>
/// — or the run running out of no-progress budget), the retry's model floor is raised above the prior attempt's own
/// effective tier. Pure + stateless: given only the evidence + the candidate pool, always the same pick.
///
/// <para>Deliberately the INVERSE of <see cref="AgentPlaneModelRanking"/>'s own "avoid Frontier by default" policy
/// (ordinary execution doesn't need the priciest tier — it's verified downstream by the task's own acceptance
/// check): escalation exists PRECISELY because that check just proved this specific unit needs more. It runs even
/// over a profile's explicitly-pinned model name — an operator's ordinary choice is a floor for untested work, not
/// a ceiling once the run's own evidence disproves it. It still respects the SAME <c>IsDefault</c> row-star
/// precedence <see cref="AgentPlaneModelRanking.Rank"/> gives an unpinned auto-pick, and the caller (<see
/// cref="Executors.RealSupervisorActionExecutor"/>) never even attempts a pick once the run is already over its
/// cost cap.</para>
/// </summary>
public static class SupervisorRetryEscalation
{
    /// <summary>
    /// Why a retry should escalate, or null when it shouldn't. Contradiction is checked FIRST (a concrete, per-unit
    /// verdict) — the no-progress proximity trigger only fires once no contradiction evidence exists, so the two
    /// never double-report for the same retry. Proximity = one MORE no-progress decision away from the run's own
    /// force-stop cap — the last turn escalation could still change the outcome before the run parks for good.
    /// </summary>
    public static string? EscalationReason(string? priorContradiction, int noProgressDecisions, int maxNoProgressDecisions)
    {
        if (priorContradiction is not null)
            return $"the prior attempt's self-report contradicted its acceptance grade ({priorContradiction})";

        if (noProgressDecisions >= maxNoProgressDecisions - 1)
            return $"the run is {noProgressDecisions} consecutive decision(s) without progress, one away from its no-progress cap ({maxNoProgressDecisions})";

        return null;
    }

    /// <summary>
    /// Pick the STRONGEST candidate in <paramref name="pool"/> whose effective tier beats the prior model's — never
    /// avoiding <see cref="ModelCapabilityTier.Frontier"/> (the whole point of escalating), but still <c>IsDefault</c>-first
    /// among the qualifying candidates, mirroring <see cref="AgentPlaneModelRanking.Rank"/>'s own precedence. The
    /// prior model's tier is the HIGHEST effective tier among any current pool row sharing its name (case-insensitive
    /// — a model can be credentialed more than once); <see cref="ModelCapabilityTier.Unknown"/> when the prior model
    /// is null, unrecognized, or no longer in the pool — so escalation can still reach for ANY tiered candidate.
    /// Null when nothing in the pool beats that floor (already at the top, or every candidate is untiered) — the
    /// caller then leaves the retry's ordinary model resolution untouched.
    /// </summary>
    public static T? PickStrongerModel<T>(IEnumerable<T> pool, Func<T, bool> isDefault, Func<T, ModelCapabilityTier?> probedTier, Func<T, ModelCapabilityTier?> declaredTier, Func<T, string> modelId, string? priorModelName)
    {
        var list = pool as IReadOnlyCollection<T> ?? pool.ToList();

        var priorTier = priorModelName is null
            ? ModelCapabilityTier.Unknown
            : list.Where(m => string.Equals(modelId(m), priorModelName, StringComparison.OrdinalIgnoreCase))
                .Select(m => AgentPlaneModelRanking.Effective(probedTier(m), declaredTier(m)))
                .DefaultIfEmpty(ModelCapabilityTier.Unknown)
                .Max();

        return list
            .Where(m => AgentPlaneModelRanking.Effective(probedTier(m), declaredTier(m)) > priorTier)
            .OrderByDescending(isDefault)
            .ThenByDescending(m => (int)AgentPlaneModelRanking.Effective(probedTier(m), declaredTier(m)))
            .ThenBy(modelId, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
