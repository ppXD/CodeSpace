using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

internal sealed record BenchmarkEvidenceOptions(string Directory, string Label, IReadOnlyList<string> KnownSecrets)
{
    internal const string DirectoryEnvVar = "CODESPACE_BENCHMARK_EVIDENCE_DIRECTORY";
    internal const int GradeReadLimitBytes = 65_536;
    internal const int MaximumSecretBytes = 4_096;
    internal const int CandidateLimit = 64;
}

internal static class BenchmarkEvidenceExport
{
    internal static BenchmarkEvidenceOptions LiveOptions(string label, IReadOnlyList<string> knownSecrets)
    {
        var root = Environment.GetEnvironmentVariable(BenchmarkEvidenceOptions.DirectoryEnvVar) ?? Path.Combine(Path.GetTempPath(), "codespace-benchmark-evidence");
        return new BenchmarkEvidenceOptions(Path.Combine(Path.GetFullPath(root), Guid.NewGuid().ToString("N")), label, knownSecrets);
    }

    internal static async Task<CorpusBenchmarkRun> RunAsync(ILifetimeScope scope, CorpusBenchmarkRequest request, BenchmarkEvidenceOptions options, CancellationToken cancellationToken)
    {
        var session = new ExportSession(options, request);
        session.WriteManifest(null);
        using var observed = scope.BeginLifetimeScope(b => b.RegisterDecorator<IBenchmarkRunner>((context, _, inner) => new ObservedRunner(inner, context.Resolve<CodeSpaceDbContext>(), context.Resolve<IArtifactRangeReader>(), session)));
        CorpusBenchmarkRun? run = null;
        try
        {
            run = await observed.Resolve<ICorpusBenchmarkRunner>().RunAsync(request, cancellationToken).ConfigureAwait(false);
            session.ExportFailures.ShouldBe(0, "benchmark evidence could not be persisted; an instrument write failure cannot be a measured pass");
            return run;
        }
        finally { session.WriteManifest(run); }
    }

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static IReadOnlyList<string> RedactionNeedles(IEnumerable<string> values)
    {
        var needles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            needles.Add(value);
            var trimmed = value.Trim();
            needles.Add(trimmed);
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
            // Credential consumers trim trailing slashes; provider errors may report only the origin or host.
            needles.Add(trimmed.TrimEnd('/'));
            needles.Add(uri.AbsoluteUri.TrimEnd('/'));
            needles.Add(uri.GetLeftPart(UriPartial.Authority));
            needles.Add(uri.Host);
        }
        return needles.ToArray();
    }

    internal static void WriteFile(string path, ReadOnlySpan<byte> bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, bytes.ToArray());
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class ObservedRunner : IBenchmarkRunner
    {
        private readonly IBenchmarkRunner _inner;
        private readonly CodeSpaceDbContext _db;
        private readonly IArtifactRangeReader _artifacts;
        private readonly ExportSession _session;
        public ObservedRunner(IBenchmarkRunner inner, CodeSpaceDbContext db, IArtifactRangeReader artifacts, ExportSession session) { _inner = inner; _db = db; _artifacts = artifacts; _session = session; }

        public async Task<BenchmarkResult> RunAsync(BenchmarkTask task, BenchmarkMode mode, BenchmarkExecutionContext context, CancellationToken cancellationToken)
        {
            BenchmarkResult? result = null;
            try { return result = await _inner.RunAsync(task, mode, context, cancellationToken).ConfigureAwait(false); }
            finally
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await _session.WriteCellAsync(new CellCapture(task, mode, context, result), _db, _artifacts, deadline.Token).ConfigureAwait(false); }
                catch
                {
                    _session.ExportFailures++;
                    // The corpus catches per-cell errors. The outer instrument assertion still fails after it returns.
                    throw;
                }
            }
        }
    }

    private sealed record CellCapture(BenchmarkTask Task, BenchmarkMode Mode, BenchmarkExecutionContext Context, BenchmarkResult? Result);

    private sealed class ExportSession
    {
        private readonly BenchmarkEvidenceOptions _options;
        private readonly CorpusBenchmarkRequest _request;
        private readonly List<object> _cells = [];
        private readonly HashSet<(string TaskId, BenchmarkMode Mode)> _exported = [];
        private readonly string _suiteVersion;
        private SecretRedactor _redactor;
        public int ExportFailures { get; set; }

        public ExportSession(BenchmarkEvidenceOptions options, CorpusBenchmarkRequest request)
        {
            _options = options;
            _request = request;
            _redactor = new SecretRedactor(RedactionNeedles(options.KnownSecrets));
            _suiteVersion = EvalSuite.ManifestFor(request.Tasks, request.SuiteContentHash).Version;
        }

        public void WriteManifest(CorpusBenchmarkRun? run)
        {
            var manifest = new
            {
                schemaVersion = 1, label = Safe(_options.Label), finished = run is not null, suiteVersion = Safe(run?.SuiteVersion ?? _suiteVersion),
                declaredCells = _request.Tasks.SelectMany(t => t.Modes.Select(m => new { taskId = Safe(t.Id), mode = m.ToString() })).ToArray(),
                unexportedCells = _request.Tasks.SelectMany(t => t.Modes.Where(m => !_exported.Contains((t.Id, m))).Select(m => new { taskId = Safe(t.Id), mode = m.ToString(), status = "unknown-unexported" })).ToArray(),
                exportedCells = _cells, exportFailures = ExportFailures,
                erroredCells = run?.Errored.Select(e => new { taskId = Safe(e.TaskId), mode = e.Mode.ToString(), status = "infra-unknown" }).ToArray(),
                scope = "direct benchmark runner; this is not full Launch qualification or a total-cost receipt",
                attemptPolicy = "Only final AgentRunId is authoritative. Workspace discoveries are not causal retry receipts; ungraded pre-respawn oracle outcomes stay unknown.",
                bounds = new { gradeReadBytes = BenchmarkEvidenceOptions.GradeReadLimitBytes, candidateRowsPerCell = BenchmarkEvidenceOptions.CandidateLimit, maximumSecretBytes = BenchmarkEvidenceOptions.MaximumSecretBytes },
            };
            WriteFile(Path.Combine(_options.Directory, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, AgentJson.Options));
        }

        public async Task WriteCellAsync(CellCapture capture, CodeSpaceDbContext db, IArtifactRangeReader artifacts, CancellationToken cancellationToken)
        {
            var ordinal = _cells.Count + 1;
            var result = capture.Result;
            AgentRun? final = null;
            AgentRun[] candidates = [];
            int? discoveredCount = null;
            var discovery = "available";
            try
            {
                if (result?.AgentRunId is { } runId) final = await db.AgentRun.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId && r.TeamId == capture.Context.TeamId, cancellationToken).ConfigureAwait(false);
                // A unique staged cwd only discovers candidates. Reviewer/other-process rows must never be counted as retries.
                var hint = JsonSerializer.Serialize(new { workspaceDirectory = capture.Context.WorkspaceDirectory }, AgentJson.Options);
                var query = db.AgentRun.AsNoTracking().Where(r => r.TeamId == capture.Context.TeamId && EF.Functions.JsonContains(r.TaskJson, hint));
                discoveredCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);
                candidates = await query.OrderBy(r => r.CreatedDate).ThenBy(r => r.Id).Take(BenchmarkEvidenceOptions.CandidateLimit).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) { discovery = "unknown-read-failed"; }

            var models = candidates.Append(final).Where(r => r is not null).Select(r => ReadResult(r!)?.Model).Where(m => !string.IsNullOrWhiteSpace(m)).Cast<string>().Distinct().ToArray();
            _redactor = _redactor.With(RedactionNeedles(models));
            var grade = await BenchmarkGradeEvidenceExport.ReadAsync(artifacts, result?.Grade, new GradeEvidenceRequest(capture.Context.TeamId, _options.Directory, $"grade-{ordinal:D4}.txt", _options.KnownSecrets.Concat(models).ToArray()), cancellationToken).ConfigureAwait(false);
            var finalResult = final is null ? null : ReadResult(final);
            var finalAttempt = final is null ? null : Attempt(final, finalResult);
            bool? firstOracle = result is { FormatFaultRespawns: 0 } && final is not null ? result.Grade.Passed : null;
            bool? firstCompletion = result is { FormatFaultRespawns: 0 } && final is not null ? final.Status == AgentRunStatus.Succeeded : null;
            var row = new
            {
                schemaVersion = 1, taskId = Safe(capture.Task.Id), mode = capture.Mode.ToString(),
                finalGradePassed = result?.Grade.Passed, finalGradeDetail = Safe(result?.Grade.Detail), finalGradeClass = result?.Grade.Class?.ToString(),
                finalAttempt, declaredRespawns = result?.FormatFaultRespawns, declaredAgentRunAttempts = result is null ? (int?)null : result.FormatFaultRespawns + 1,
                attemptUnit = "AgentRun; internal CLI revise rounds, reattachments and provider requests are not independently enumerated",
                firstOraclePassed = firstOracle, firstCompletionSucceeded = firstCompletion,
                attemptLineage = result is { FormatFaultRespawns: 0 } && final is not null ? "single-result-run" : "unknown-no-durable-retry-link",
                reportedAggregateUsage = result?.TokenUsage, aggregateUsageCompleteness = "unknown",
                usageScope = "reported agent tokens; may omit unreported usage and critic/provider costs; not a monetary total or hard cap",
                discoveryStatus = discovery, discoveredCount, candidatesTruncated = discoveredCount > candidates.Length,
                discoveredCandidates = candidates.Select(r => Attempt(r, ReadResult(r))).ToArray(), candidateMeaning = "discovery only; excluded from attempt count, scoring and usage totals",
                gradeEvidence = grade,
            };
            var name = $"cell-{ordinal:D4}.json";
            WriteFile(Path.Combine(_options.Directory, name), JsonSerializer.SerializeToUtf8Bytes(row, AgentJson.Options));
            _cells.Add(new { taskId = Safe(capture.Task.Id), mode = capture.Mode.ToString(), file = name, evidenceAvailability = grade.Availability, evidencePartial = grade.Partial });
            _exported.Add((capture.Task.Id, capture.Mode));
            WriteManifest(null);
        }

        private object Attempt(AgentRun run, AgentRunResult? result) => new
        {
            runId = run.Id, status = run.Status.ToString(), exitReason = Safe(result?.ExitReason), reviseRounds = result?.ReviseRounds, reattachAttempts = run.ReattachAttempts, reportedUsage = result?.TokenUsage,
            usageAvailability = result?.TokenUsage is null ? "unknown" : result.TokenUsage.InputTokens < 0 || result.TokenUsage.OutputTokens < 0 ? "invalid" : "reported",
            observedModelFingerprint = string.IsNullOrEmpty(result?.Model) ? null : Hash(Encoding.UTF8.GetBytes(result.Model))[..16],
        };

        private string? Safe(string? value)
        {
            if (value is null) return null;
            var redacted = _redactor.Redact(value);
            return redacted.Length > 2_048 ? redacted[..2_048] : redacted;
        }
        private static AgentRunResult? ReadResult(AgentRun run) { try { return run.ResultJson is null ? null : JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson, AgentJson.Options); } catch (JsonException) { return null; } }
    }
}
