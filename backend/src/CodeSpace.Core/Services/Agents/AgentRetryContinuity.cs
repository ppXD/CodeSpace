namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// What a retried agent is told about the WORLD STATE its restored conversation refers to — the one place both retry
/// lanes read, so "your prior work is not here" can never mean two different things to a model. The quick lane's
/// <c>agent.run</c> respawn and the supervisor's <c>retry</c> resolve their world-state pin from different sources (a
/// resume payload vs. a <c>PublishManifest</c> row), but the SENTENCE they append when nothing was preserved must be
/// identical — a second wording would be a second contract (Rule 7, the same "recognise it in ONE place" discipline
/// as <see cref="AgentModelEscalationTrigger"/>).
/// </summary>
public static class AgentRetryContinuity
{
    /// <summary>The honest-redo line: fires ONLY when a resumed conversation exists but the workspace was NOT pinned to a prior pushed branch — never on a genuine cold-start retry (no prior attempt at all), which stays byte-identical.</summary>
    public const string HonestNoContinuityHint = "Note: your prior attempt's conversation is restored, but its git changes were NOT preserved in this workspace (no pushed branch was found to continue from) — you must redo any relevant file changes from scratch.";

    /// <summary>Append <see cref="HonestNoContinuityHint"/> to a resumed task's goal. One composition, so the two lanes cannot drift on the separator either.</summary>
    public static string WithHonestNoContinuityHint(string goal) => $"{goal}\n\n{HonestNoContinuityHint}";
}
