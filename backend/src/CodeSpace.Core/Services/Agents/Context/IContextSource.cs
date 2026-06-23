using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Context;

/// <summary>
/// ONE retrievable body of context an agent can PULL mid-run (Rule 7 — a narrow, single-responsibility contract) — the
/// complement to the digest the run is PUSHED at launch. The <c>get_context</c> tool is a thin dispatcher over a
/// registry of these; the tool never branches on what is being retrieved, it only resolves the run's scope and forwards
/// the <see cref="AgentContextQuery"/>. A source erases its retrieval dimension (prior turns / rolling summary / a future
/// repo or semantic index) into uniform <see cref="AgentContextResult"/> text — so a NEW source ships by adding an impl
/// under <c>Context/Sources/</c> + a new <see cref="Kind"/> string, with ZERO edit to the tool or the registry
/// (Rule 18.3, the variant axis).
///
/// <para>A source NEVER throws for a clean miss (no session, no matching content) — it returns
/// <see cref="AgentContextResult.Empty"/>. It MUST re-key every read on <see cref="AgentContextQuery.TeamId"/>
/// (fail-closed tenancy) and bound its own output so one pull can't blow up the model's context.</para>
/// </summary>
public interface IContextSource
{
    /// <summary>Stable open-string key the agent names + the registry indexes on (e.g. "session.turns", "session.summary").</summary>
    string Kind { get; }

    /// <summary>One-line description surfaced in the tool's schema so the model knows what this source returns.</summary>
    string Description { get; }

    /// <summary>Retrieve this source's context for the (already scope-resolved) query. Returns <see cref="AgentContextResult.Empty"/> on a clean miss — never throws for "nothing here".</summary>
    Task<AgentContextResult> RetrieveAsync(AgentContextQuery query, CancellationToken cancellationToken);
}
