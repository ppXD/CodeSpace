using System.Text.Json;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// Operator override: force-resolve a STRANDED signal-driven wait (a <c>Timer</c> whose scheduled wake was dropped, or
/// a <c>Callback</c> whose external system never posted) on a Suspended run, so the run un-strands and continues. Reuses
/// the same idempotent resolve-first CAS every real resume signal funnels through.
///
/// <para>Tenancy: the wait's run must belong to the caller's current team (404 conflated with not-yours / not-found).</para>
///
/// <para>Refuses a decision-driven (<c>Approval</c>/<c>Action</c>/<c>Decision</c>) or completion-driven
/// (<c>Subworkflow</c>/<c>AgentRun</c>/supervisor) wait — those resolve via their own verb or when their real work
/// completes (faking their payload here would corrupt the node). Every genuine reissue writes a <c>wait.reissued</c>
/// audit record naming the operator.</para>
/// </summary>
public sealed record ReissueWaitCommand : ICommand<ReissueWaitOutcome>, IRequireTeamMembership
{
    public Guid RunId { get; init; }

    public Guid WaitId { get; init; }

    /// <summary>For a <c>Callback</c> wait — the body to resolve it with, surfaced as the node's <c>body</c> output. Ignored for a <c>Timer</c> wake (fired with the standard wake marker). Absent → an empty body.</summary>
    public JsonElement? Body { get; init; }
}
