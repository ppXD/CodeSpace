using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;

/// <summary>
/// The STRUCTURED-ARTIFACT oracle (<see cref="BenchmarkGradingKind.ArtifactSchema"/>, triad S7) — deterministic
/// acceptance for work whose deliverable is DATA (an extraction, a config, a dataset): every declared deliverable
/// file must parse as JSON and validate against the acceptance's authored schema. Validation is the shared focused
/// checker (<see cref="JsonSchemaValidator"/> — required / type / enum / nested properties+items, deliberately not
/// full conformance), the same one that guards structured LLM output, so both ends of the platform reject the same
/// garbage the same way.
///
/// <para>FAIL-CLOSED: no workspace, no schema, no paths, an unreadable/escaping file, a parse failure, and any
/// schema violation all fail with a detail naming the file + violations — what the S6 revise loop feeds back.</para>
/// </summary>
public sealed class ArtifactSchemaGrader : IBenchmarkGrader, ISingletonDependency
{
    /// <summary>Per-file read cap — a graded data artifact past 1 MiB is a blob, not a deliverable to validate inline.</summary>
    internal const int MaxArtifactBytesPerFile = 1024 * 1024;

    public BenchmarkGradingKind Kind => BenchmarkGradingKind.ArtifactSchema;

    public Task<BenchmarkGrade> GradeAsync(BenchmarkGradingContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(context.WorkspaceDirectory)) return Task.FromResult(Fail("no-workspace"));
        if (context.Acceptance?.Schema is not { ValueKind: JsonValueKind.Object } schema) return Task.FromResult(Fail("no-schema", GradeFailureClass.SpecIncomplete));

        var paths = context.Task.TestCommand;

        if (paths.Count == 0) return Task.FromResult(Fail("no-artifact-paths"));

        var root = Path.GetFullPath(context.WorkspaceDirectory);

        foreach (var path in paths)
        {
            if (!WorkspaceArtifactGuard.TryReadWithin(root, path, MaxArtifactBytesPerFile, out var content, out var error))
                return Task.FromResult(Fail(error!));

            JsonDocument document;
            try { document = JsonDocument.Parse(content); }
            catch (JsonException ex) { return Task.FromResult(Fail($"artifact-not-json: {path} — {ex.Message}")); }

            using (document)
            {
                var violations = JsonSchemaValidator.Validate(document.RootElement, schema);

                if (violations.Count > 0)
                    return Task.FromResult(Fail($"schema-violations: {path} — {string.Join("; ", violations)}"));
            }
        }

        return Task.FromResult(new BenchmarkGrade { Passed = true, Detail = $"schema-valid: {paths.Count} artifact(s)" });
    }

    private static BenchmarkGrade Fail(string detail, GradeFailureClass? failureClass = null) => new() { Passed = false, Detail = detail, Class = failureClass };
}
