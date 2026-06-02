namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// Inputs for <see cref="MissingRequiredInputValidator.EnsureSatisfied"/>.
///
/// <list type="bullet">
///   <item><c>RequiredInputNames</c> — names of every input declared with
///         <c>Required = true</c> in the workflow definition (e.g. extracted via
///         <c>definition.Inputs.Where(i =&gt; i.Required).Select(i =&gt; i.Name)</c>).</item>
///   <item><c>ResolvedInputNames</c> — names that <c>WorkflowEngine.BuildInputScope</c>
///         actually populated in the <c>{{input.*}}</c> bag (caller-supplied OR
///         default-filled). The diff against <c>RequiredInputNames</c> is the missing set
///         the validator inspects.</item>
///   <item><c>TeamId</c>, <c>WorkflowId</c> — included verbatim in the exception text so
///         operators triaging a Failed run can pin the source from the run row alone.</item>
/// </list>
/// </summary>
public sealed record MissingRequiredInputContext(IReadOnlyCollection<string> RequiredInputNames, IReadOnlyCollection<string> ResolvedInputNames, Guid TeamId, Guid WorkflowId);

/// <summary>
/// Fail-fast guard for the silent-null bug on workflow runs that don't supply a value for an
/// input marked <c>Required = true</c>. Before this guard the engine silently omitted the
/// missing key from <c>BuildInputScope</c>'s output and <c>VariableResolver</c> resolved
/// <c>{{input.missing_name}}</c> to <c>null</c> — runs completed "Success" with empty/null
/// values cascading into node outputs.
///
/// <para>Invoked from <c>WorkflowEngine.BuildScopeFreshAndPersistSnapshotAsync</c> AFTER
/// <c>BuildInputScope</c>, so it has both the declared required-set AND the actually-populated
/// set. Throws <see cref="MissingRequiredInputException"/> on any missing required input so the
/// run lands in Failed with a clear cause instead of silently producing corrupted output.
/// Skipped on replay paths — replays reuse the first-run snapshot scope, so a re-validation
/// could only ever surface a discrepancy that the original run already enforced.</para>
/// </summary>
public static class MissingRequiredInputValidator
{
    public static void EnsureSatisfied(MissingRequiredInputContext context)
    {
        // Hot-path fast exit: most workflows have no Required inputs, or all required
        // inputs have defaults — diff only when there's something to diff.
        if (context.RequiredInputNames.Count == 0) return;

        var resolved = context.ResolvedInputNames as HashSet<string> ?? new HashSet<string>(context.ResolvedInputNames, StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var name in context.RequiredInputNames)
            if (!resolved.Contains(name)) missing.Add(name);

        if (missing.Count == 0) return;

        // Stable order so the operator-facing message is deterministic across runs.
        missing.Sort(StringComparer.Ordinal);

        throw new MissingRequiredInputException(
            $"Workflow {context.WorkflowId} declares required input(s) [{string.Join(", ", missing)}] that were not supplied by the run request and have no Default in team {context.TeamId}. " +
            $"Add a Default to the declaration, or supply the value at run time.");
    }
}
