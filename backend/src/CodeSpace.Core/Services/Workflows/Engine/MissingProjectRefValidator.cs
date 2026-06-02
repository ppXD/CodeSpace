namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// Inputs for <see cref="MissingProjectRefValidator.EnsureKnown"/>.
///
/// <list type="bullet">
///   <item><c>ReferencedSlugs</c> — slugs the workflow definition mentions
///         (extracted by <c>WorkflowEngine.CollectReferencedProjectSlugs</c>).</item>
///   <item><c>FoundSlugs</c> — slugs the DB lookup returned. Subset of referenced
///         under normal operation; the diff is what the validator inspects.</item>
///   <item><c>TeamId</c>, <c>WorkflowId</c> — included verbatim in the exception text so
///         operators triaging a Failed run can pin the source from the run row alone.</item>
/// </list>
/// </summary>
public sealed record MissingProjectRefContext(IReadOnlyCollection<string> ReferencedSlugs, IReadOnlyCollection<string> FoundSlugs, Guid TeamId, Guid WorkflowId);

/// <summary>
/// Fail-fast guard for the silent-null bug on workflow runs that reference deleted projects.
/// Before this guard the engine pre-resolved <c>project.{slug}.X</c> refs to <c>null</c> if
/// <c>{slug}</c> was missing from the team — runs completed "Success" with empty/null values
/// in their outputs. The bug equally affected fresh runs (project deleted before first run)
/// AND replays (project deleted between original run and replay).
///
/// <para>Invoked from <c>WorkflowEngine.LoadReferencedProjectVariablesAsync</c> AFTER the
/// slug→project DB lookup, so it has both the set referenced by the definition AND the set
/// actually found in the database. Throws <see cref="MissingProjectRefException"/> on any
/// missing slug so the run lands in Failed with a clear cause instead of silently producing
/// corrupted output.</para>
/// </summary>
public static class MissingProjectRefValidator
{
    public static void EnsureKnown(MissingProjectRefContext context)
    {
        // Hot-path fast exit: most runs reference projects that exist. Build the diff
        // only when there's something to diff (cheap HashSet lookup per ref).
        if (context.ReferencedSlugs.Count == 0) return;

        var found = context.FoundSlugs as HashSet<string> ?? new HashSet<string>(context.FoundSlugs, StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var slug in context.ReferencedSlugs)
            if (!found.Contains(slug)) missing.Add(slug);

        if (missing.Count == 0) return;

        // Stable order so the operator-facing message is deterministic across runs.
        missing.Sort(StringComparer.Ordinal);

        throw new MissingProjectRefException(
            $"Workflow {context.WorkflowId} references project slug(s) [{string.Join(", ", missing)}] that do not exist in team {context.TeamId}. " +
            $"Remove the stale refs from the workflow definition.");
    }
}
