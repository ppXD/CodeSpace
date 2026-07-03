using System.Text.RegularExpressions;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;

/// <summary>
/// The CITATIONS oracle (<see cref="BenchmarkGradingKind.CitationsResolve"/>, triad S7) — a deterministic,
/// agent-independent check for research output: every declared deliverable file must CONTAIN at least one markdown
/// citation, every relative-path citation must resolve to a real file inside the produced workspace (resolved against
/// the CITING file's directory, markdown semantics, clamped by the shared guard), and every URL citation must be a
/// well-formed absolute http(s) link. Deliberately NETWORK-FREE: the oracle must stay deterministic and
/// egress-independent, so link LIVENESS is a judge/critic concern — existence and well-formedness are the code's.
///
/// <para>FAIL-CLOSED: no workspace, no paths, an unreadable deliverable, a citation-free deliverable, an unresolvable
/// file target, a non-http(s) scheme (<c>mailto:</c>, <c>javascript:</c>, a bare word) all fail with a detail naming
/// the file + target — exactly what the S6 revise loop feeds back. A pure <c>#anchor</c> self-link is accepted
/// (heading anchors aren't verifiable without a markdown AST — documented bound, not a silent gap).</para>
/// </summary>
public sealed partial class CitationsResolveGrader : IBenchmarkGrader, ISingletonDependency
{
    /// <summary>Per-file read cap — a graded deliverable is prose, not a blob; over-cap content is truncated (citations past the cap are honestly not seen, and the truncation marker never parses as one).</summary>
    internal const int MaxArtifactBytesPerFile = 1024 * 1024;

    public BenchmarkGradingKind Kind => BenchmarkGradingKind.CitationsResolve;

    public Task<BenchmarkGrade> GradeAsync(BenchmarkGradingContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(context.WorkspaceDirectory)) return Task.FromResult(Fail("no-workspace"));

        var paths = context.Task.TestCommand;

        if (paths.Count == 0) return Task.FromResult(Fail("no-artifact-paths"));

        var root = Path.GetFullPath(context.WorkspaceDirectory);
        var total = 0;

        foreach (var path in paths)
        {
            if (!WorkspaceArtifactGuard.TryReadWithin(root, path, MaxArtifactBytesPerFile, out var content, out var error))
                return Task.FromResult(Fail(error!));

            var citations = ExtractCitations(content);

            if (citations.Count == 0) return Task.FromResult(Fail($"citations-missing: {path} contains no markdown citation"));

            foreach (var target in citations)
                if (ResolveCitation(root, path, target) is { } broken)
                    return Task.FromResult(Fail($"citation-unresolvable: {path} → {target} ({broken})"));

            total += citations.Count;
        }

        return Task.FromResult(new BenchmarkGrade { Passed = true, Detail = $"citations-resolve: {total} citation(s) across {paths.Count} file(s)" });
    }

    /// <summary>Every markdown-link TARGET in the content — <c>[text](target)</c>, with an optional <c>"title"</c> after the target dropped. Internal for direct unit pinning.</summary>
    internal static IReadOnlyList<string> ExtractCitations(string content) =>
        MarkdownLink().Matches(content).Select(m => m.Groups[1].Value.Trim()).Where(t => t.Length > 0).ToList();

    /// <summary>
    /// Null when the citation target RESOLVES; else the reason it doesn't. An absolute http(s) URL must parse with a
    /// host; a pure <c>#anchor</c> is accepted (documented bound); any other scheme fails; a relative path resolves
    /// against the CITING file's directory (markdown semantics) with an optional <c>#fragment</c> stripped, and must
    /// exist within the workspace per the shared guard. Internal for direct unit pinning.
    /// </summary>
    internal static string? ResolveCitation(string root, string citingFile, string target)
    {
        if (target.StartsWith('#')) return null;   // a self-anchor — heading existence needs an AST; accepted, documented

        if (Uri.TryCreate(target, UriKind.Absolute, out var absolute))
            return absolute.Scheme is "http" or "https"
                ? (string.IsNullOrEmpty(absolute.Host) ? "url-has-no-host" : null)
                : $"unsupported-scheme:{absolute.Scheme}";

        // Relative path — resolve against the citing file's directory, strip a #fragment, clamp to the workspace.
        var withoutFragment = target.Split('#', 2)[0];

        if (withoutFragment.Length == 0) return "empty-path";

        var citingDir = Path.GetDirectoryName(citingFile) ?? "";
        var relativeToRoot = Path.Combine(citingDir, withoutFragment);

        return WorkspaceArtifactGuard.ExistsWithin(root, relativeToRoot) ? null : "file-not-found-in-workspace";
    }

    /// <summary>Markdown inline link: <c>[text](target)</c> — the target group stops at whitespace or <c>)</c> so an optional <c>"title"</c> is excluded. An image (<c>![alt](src)</c>) matches too — an image source is a citation-grade reference.</summary>
    [GeneratedRegex("""\[[^\]]*\]\(\s*<?([^)\s>]+)>?[^)]*\)""")]
    private static partial Regex MarkdownLink();

    private static BenchmarkGrade Fail(string detail) => new() { Passed = false, Detail = detail };
}
