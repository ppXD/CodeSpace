using CodeSpace.Core.Hardening;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// Hardening check (CLAUDE.md Rule 11) for the silent-null bug on workflow runs that
/// reference deleted projects. Before this fix the engine pre-resolved
/// <c>project.{slug}.X</c> refs to <c>null</c> if <c>{slug}</c> was missing from the
/// team — runs completed "Success" with empty/null values in their outputs. The bug
/// equally affected fresh runs (project deleted before first run) AND replays (project
/// deleted between original run and replay).
///
/// <para>Validator is invoked from <c>WorkflowEngine.LoadReferencedProjectVariablesAsync</c>
/// AFTER the slug→project DB lookup, so it has both the set referenced by the definition
/// AND the set actually found in the database. The diff is the missing set.</para>
///
/// <para>Three-mode enforcement via <see cref="EnforcementEnvVar"/>:
/// <list type="bullet">
///   <item><c>off</c>    — silent allow (legacy behavior; the resolver still returns null
///                         for those refs). Use only if your workflows intentionally
///                         reference projects that may or may not exist at run time.</item>
///   <item><c>warn</c>   — DEFAULT. Log a structured warning naming every missing slug
///                         and the env var to flip to strict, then return as if Off.
///                         Surfaces the latent bug in logs without breaking deploys.</item>
///   <item><c>strict</c> — throw <see cref="MissingProjectRefException"/>. The run lands
///                         in Failed status with a clear cause instead of silently
///                         producing corrupted outputs. Recommended for production once
///                         operators have remediated stale refs.</item>
/// </list></para>
/// </summary>
public static class MissingProjectRefValidator
{
    /// <summary>
    /// Env var name that flips the enforcement mode. Renaming this constant breaks every
    /// operator who pinned the knob via env — pinned by a unit test (CLAUDE.md Rule 8)
    /// so the rename becomes a compile-time-visible decision.
    /// </summary>
    public const string EnforcementEnvVar = "CODESPACE_MISSING_PROJECT_REF_ENFORCEMENT";

    public static void EnsureKnown(
        IReadOnlyCollection<string> referencedSlugs,
        IReadOnlyCollection<string> foundSlugs,
        Guid teamId,
        Guid workflowId,
        EnforcementMode mode,
        ILogger logger)
    {
        // Hot-path fast exit: most runs reference projects that exist. Build the diff
        // only when there's something to diff (cheap HashSet lookup per ref).
        if (referencedSlugs.Count == 0) return;

        var found = foundSlugs as HashSet<string> ?? new HashSet<string>(foundSlugs, StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var slug in referencedSlugs)
            if (!found.Contains(slug)) missing.Add(slug);

        if (missing.Count == 0) return;

        // Stable order for deterministic test assertions + log readability.
        missing.Sort(StringComparer.Ordinal);

        switch (mode)
        {
            case EnforcementMode.Off:
                return;

            case EnforcementMode.Warn:
                logger.LogWarning(
                    "Workflow {WorkflowId} references project slug(s) {MissingSlugs} that do not exist in team {TeamId}. " +
                    "These refs will resolve to null at run time. Set {EnvVar}=strict to fail-fast on missing refs " +
                    "(recommended for production), or remove the stale refs from the workflow definition. " +
                    "Currently allowed for backward compat.",
                    workflowId, missing, teamId, EnforcementEnvVar);
                return;

            case EnforcementMode.Strict:
                throw new MissingProjectRefException(
                    $"Workflow {workflowId} references project slug(s) [{string.Join(", ", missing)}] that do not exist in team {teamId}. " +
                    $"Remove the stale refs from the workflow definition, OR set {EnforcementEnvVar}=warn (allow with log) " +
                    $"or {EnforcementEnvVar}=off (silent).");

            default:
                // Defence-in-depth — EnforcementModeReader can't produce a value outside
                // the enum, but a future code path that constructs the mode manually could.
                throw new ArgumentOutOfRangeException(nameof(mode), mode, $"Unknown enforcement mode for {EnforcementEnvVar}");
        }
    }
}
