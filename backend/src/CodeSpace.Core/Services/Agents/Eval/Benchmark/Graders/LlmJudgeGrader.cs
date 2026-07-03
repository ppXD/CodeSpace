using System.Text;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Review;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Review;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;

/// <summary>
/// The RUBRIC LLM-judge oracle (<see cref="BenchmarkGradingKind.LlmJudge"/>, triad S7) — objective acceptance for
/// work whose "done" cannot be a passing test: read the declared deliverable file(s) off the agent-independent clone,
/// have an independent judge model answer each rubric criterion with a BINARY met/not-met + evidence, then aggregate
/// the WEIGHTED met-fraction against the rubric's threshold. The subjective step is contained and auditable (narrow
/// per-criterion questions, evidence quoted, temperature 0); the aggregation is pure math owned here.
///
/// <para>FAIL-CLOSED end to end: no workspace / no team / no rubric / no deliverable paths / a missing or escaping
/// file all fail with a named detail; a judge that cannot produce a complete verdict fails as <c>grade-error:</c> —
/// the INFRA classification, so the S6 revise loop never burns an agent round on a broken judge, while a genuine
/// rubric failure (criteria unmet) IS agent-fixable and does feed the loop.</para>
/// </summary>
public sealed class LlmJudgeGrader : IBenchmarkGrader, ISingletonDependency
{
    /// <summary>Per-file read cap — a judged artifact must never balloon the judge prompt (over-cap content is truncated with a visible marker).</summary>
    internal const int MaxArtifactBytesPerFile = 128 * 1024;

    /// <summary>The default threshold when the rubric names none: EVERY criterion must be met — the strictest, safest reading of "done".</summary>
    internal const double DefaultThreshold = 1.0;

    // The judge (scoped — it resolves the model pool through the DbContext) is minted per grade from a fresh scope,
    // the executor's heartbeat-loop pattern, so this grader stays a singleton the registry can hold.
    private readonly IServiceScopeFactory _scopeFactory;

    public LlmJudgeGrader(IServiceScopeFactory scopeFactory) { _scopeFactory = scopeFactory; }

    public BenchmarkGradingKind Kind => BenchmarkGradingKind.LlmJudge;

    public async Task<BenchmarkGrade> GradeAsync(BenchmarkGradingContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.WorkspaceDirectory)) return Fail("no-workspace");
        if (context.TeamId is not { } teamId) return Fail("grade-error: no-team — the rubric judge needs the run's team to resolve a model");
        if (context.Acceptance?.Rubric is not { Criteria.Count: > 0 } rubric) return Fail("no-rubric");

        var paths = context.Task.TestCommand;

        if (paths.Count == 0) return Fail("no-artifact-paths");

        var (artifact, readError) = ReadDeliverables(Path.GetFullPath(context.WorkspaceDirectory), paths);

        if (artifact is null) return Fail(readError!);

        var verdict = await JudgeAsync(rubric, artifact, context.Task.Goal, teamId, cancellationToken).ConfigureAwait(false);

        if (verdict.Failed) return Fail($"grade-error: {verdict.FailureDetail}");

        return Aggregate(rubric, verdict);
    }

    /// <summary>Read every declared deliverable through the shared containment guard into ONE judged artifact (a file header per deliverable). Stateless (this grader is a singleton graded concurrently) — the fail-closed detail rides the tuple, never a field.</summary>
    private static (string? Artifact, string? Error) ReadDeliverables(string root, IReadOnlyList<string> paths)
    {
        var builder = new StringBuilder();

        foreach (var path in paths)
        {
            if (!WorkspaceArtifactGuard.TryReadWithin(root, path, MaxArtifactBytesPerFile, out var content, out var error))
                return (null, error);

            builder.AppendLine($"=== {path} ===");
            builder.AppendLine(content);
            builder.AppendLine();
        }

        return (builder.ToString(), null);
    }

    private async Task<RubricJudgeVerdict> JudgeAsync(AcceptanceRubric rubric, string artifact, string? goal, Guid teamId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IRubricJudge>()
            .JudgeAsync(rubric, artifact, string.IsNullOrWhiteSpace(goal) ? null : goal, teamId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The pass/fail math (pure, internal — unit-pinned directly): weighted met-fraction vs threshold. Negative
    /// weights clamp to 0; an all-zero-weight rubric is ungradable (fail-closed — a contract that weighs nothing
    /// decides nothing); the threshold clamps into (0, 1]. The detail names the score AND every unmet criterion with
    /// the judge's evidence — what the S6 revise loop feeds back to the agent.
    /// </summary>
    internal static BenchmarkGrade Aggregate(AcceptanceRubric rubric, RubricJudgeVerdict verdict)
    {
        var met = verdict.Criteria.Where(c => c.Met).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

        var totalWeight = 0.0;
        var metWeight = 0.0;

        foreach (var criterion in rubric.Criteria)
        {
            var weight = Math.Max(0, criterion.Weight ?? 1);
            totalWeight += weight;
            if (met.Contains(criterion.Id)) metWeight += weight;
        }

        if (totalWeight <= 0) return Fail("no-effective-weight: every rubric criterion weighs zero");

        var threshold = Math.Clamp(rubric.Threshold ?? DefaultThreshold, double.Epsilon, 1.0);
        var score = metWeight / totalWeight;
        var passed = score + 1e-9 >= threshold;   // float-safe: 0.9 of thirds must not flunk on representation error

        return passed
            ? new BenchmarkGrade { Passed = true, Detail = $"rubric {score:0.00} ≥ {threshold:0.00} — {met.Count}/{rubric.Criteria.Count} criteria met" }
            : new BenchmarkGrade { Passed = false, Detail = RenderFailure(rubric, verdict, score, threshold) };
    }

    /// <summary>The failing detail: the score line + every UNMET criterion with its requirement and the judge's evidence, bounded so the revise instruction stays readable.</summary>
    private static string RenderFailure(AcceptanceRubric rubric, RubricJudgeVerdict verdict, double score, double threshold)
    {
        const int maxDetailChars = 900;

        var unmetById = verdict.Criteria.Where(c => !c.Met).ToDictionary(c => c.Id, StringComparer.Ordinal);
        var builder = new StringBuilder($"rubric {score:0.00} < {threshold:0.00} — not met:");

        foreach (var criterion in rubric.Criteria)
        {
            if (!unmetById.TryGetValue(criterion.Id, out var c)) continue;

            var evidence = string.IsNullOrWhiteSpace(c.Evidence) ? "" : $" ({c.Evidence})";
            builder.Append($" [{criterion.Id}] {criterion.Requirement}{evidence};");

            if (builder.Length > maxDetailChars) return builder.ToString(0, maxDetailChars) + " …";
        }

        return builder.ToString().TrimEnd(';');
    }

    private static BenchmarkGrade Fail(string detail) => new() { Passed = false, Detail = detail };
}
