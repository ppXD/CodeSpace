using CodeSpace.Core.Hardening;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// Inputs for <see cref="MissingRequiredInputValidator.EnsureSatisfied"/>. Consolidates the
/// four data fields so the validator's signature stays single-line (CLAUDE.md Rule 1 —
/// 5-param cap; we'd otherwise hit 6 with mode + logger added).
///
/// <list type="bullet">
///   <item><c>RequiredInputNames</c> — names of every input declared with
///         <c>Required = true</c> in the workflow definition (e.g. extracted via
///         <c>definition.Inputs.Where(i =&gt; i.Required).Select(i =&gt; i.Name)</c>).</item>
///   <item><c>ResolvedInputNames</c> — names that <c>WorkflowEngine.BuildInputScope</c>
///         actually populated in the <c>{{input.*}}</c> bag (caller-supplied OR
///         default-filled). The diff against <c>RequiredInputNames</c> is the missing set
///         the validator inspects.</item>
///   <item><c>TeamId</c>, <c>WorkflowId</c> — included verbatim in the warning/exception
///         text so operators triaging a Failed run can pin the source from log alone.</item>
/// </list>
/// </summary>
public sealed record MissingRequiredInputContext(IReadOnlyCollection<string> RequiredInputNames, IReadOnlyCollection<string> ResolvedInputNames, Guid TeamId, Guid WorkflowId);

/// <summary>
/// Hardening check (CLAUDE.md Rule 11) for the silent-null bug on workflow runs that
/// don't supply a value for an input marked <c>Required = true</c>. Before this fix the
/// engine silently omitted the missing key from <c>BuildInputScope</c>'s output and
/// <c>VariableResolver</c> resolved <c>{{input.missing_name}}</c> to <c>null</c>. Runs
/// completed "Success" with empty/null values cascading into node outputs.
///
/// <para>Validator is invoked from <c>WorkflowEngine.BuildScopeFreshAndPersistSnapshotAsync</c>
/// AFTER <c>BuildInputScope</c>, so it has both the declared required-set AND the
/// actually-populated set. Skipped on replay paths — replays use the snapshotted scope
/// from first-run and re-validating could spuriously fail a replay if the enforcement
/// mode flipped between runs.</para>
///
/// <para>Three-mode enforcement via <see cref="EnforcementEnvVar"/>:
/// <list type="bullet">
///   <item><c>off</c>    — silent allow (legacy behavior; the resolver still returns null
///                         for the missing input).</item>
///   <item><c>warn</c>   — DEFAULT. Log a structured warning naming every missing input
///                         and the env var to flip to strict, then return as if Off.
///                         Surfaces the latent bug in logs without breaking deploys.</item>
///   <item><c>strict</c> — throw <see cref="MissingRequiredInputException"/>. The run
///                         lands in Failed status with a clear cause instead of silently
///                         producing corrupted outputs. Recommended for production once
///                         operators have made sure every Required input has a default
///                         or caller-supply mechanism.</item>
/// </list></para>
/// </summary>
public static class MissingRequiredInputValidator
{
    /// <summary>
    /// Env var name that flips the enforcement mode. Renaming this constant breaks every
    /// operator who pinned the knob via env — pinned by a unit test (CLAUDE.md Rule 8) so
    /// the rename becomes a compile-time-visible decision.
    /// </summary>
    public const string EnforcementEnvVar = "CODESPACE_MISSING_REQUIRED_INPUT_ENFORCEMENT";

    public static void EnsureSatisfied(MissingRequiredInputContext context, EnforcementMode mode, ILogger logger)
    {
        // Hot-path fast exit: most workflows have no Required inputs, or all required
        // inputs have defaults — diff only when there's something to diff.
        if (context.RequiredInputNames.Count == 0) return;

        var resolved = context.ResolvedInputNames as HashSet<string> ?? new HashSet<string>(context.ResolvedInputNames, StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var name in context.RequiredInputNames)
            if (!resolved.Contains(name)) missing.Add(name);

        if (missing.Count == 0) return;

        // Stable order for deterministic test assertions + log readability.
        missing.Sort(StringComparer.Ordinal);

        switch (mode)
        {
            case EnforcementMode.Off:
                return;

            case EnforcementMode.Warn:
                logger.LogWarning(
                    "Workflow {WorkflowId} declares required input(s) {MissingInputs} that were not supplied by the run request " +
                    "and have no Default in team {TeamId}. These inputs will resolve to null at run time. Set {EnvVar}=strict to fail-fast on missing " +
                    "required inputs (recommended for production), or add a Default to the input declaration, or update callers to always supply the value. " +
                    "Currently allowed for backward compat.",
                    context.WorkflowId, missing, context.TeamId, EnforcementEnvVar);
                return;

            case EnforcementMode.Strict:
                throw new MissingRequiredInputException(
                    $"Workflow {context.WorkflowId} declares required input(s) [{string.Join(", ", missing)}] that were not supplied by the run request and have no Default in team {context.TeamId}. " +
                    $"Add a Default to the declaration, supply the value at run time, OR set {EnforcementEnvVar}=warn (allow with log) " +
                    $"or {EnforcementEnvVar}=off (silent).");

            default:
                // Defence-in-depth — EnforcementModeReader can't produce a value outside
                // the enum, but a future code path that constructs the mode manually could.
                throw new ArgumentOutOfRangeException(nameof(mode), mode, $"Unknown enforcement mode for {EnforcementEnvVar}");
        }
    }
}
