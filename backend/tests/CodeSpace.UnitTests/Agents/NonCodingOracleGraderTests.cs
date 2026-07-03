using System.Text.Json;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Review;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Review;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The S7 non-coding oracles' PURE logic — the rubric aggregation math, the citation extraction/resolution rules,
/// the schema validation wiring, and every fail-closed guard (no workspace / no payload / missing or escaping file).
/// Real temp-dir filesystem for the containment paths (the guard's whole point is real symlink/escape behaviour);
/// the judge is faked at the <c>IRubricJudge</c> seam for the one grader that calls a model.
/// </summary>
[Trait("Category", "Unit")]
public sealed class NonCodingOracleGraderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-noncoding-oracle-" + Guid.NewGuid().ToString("N"));

    public NonCodingOracleGraderTests() { Directory.CreateDirectory(_root); }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // ─── LlmJudgeGrader.Aggregate (the pass/fail math) ───────────────────────

    [Fact]
    public void All_criteria_met_passes_at_the_default_threshold()
    {
        var rubric = Rubric(("a", null), ("b", null));
        var grade = LlmJudgeGrader.Aggregate(rubric, Verdict(("a", true), ("b", true)));

        grade.Passed.ShouldBeTrue();
        grade.Detail.ShouldContain("2/2 criteria met");
    }

    [Fact]
    public void One_unmet_criterion_fails_the_default_all_must_pass_threshold()
    {
        var rubric = Rubric(("a", null), ("b", null));
        var grade = LlmJudgeGrader.Aggregate(rubric, Verdict(("a", true), ("b", false)));

        grade.Passed.ShouldBeFalse();
        grade.Detail.ShouldContain("[b]", customMessage: "the failing detail names the unmet criterion — the revise loop's food");
    }

    [Fact]
    public void Weights_drive_the_aggregate_against_an_explicit_threshold()
    {
        // met weight 3 of total 4 = 0.75: passes a 0.7 threshold, fails 0.8.
        var rubric = Rubric(("heavy", 3.0), ("light", 1.0));

        LlmJudgeGrader.Aggregate(rubric with { Threshold = 0.7 }, Verdict(("heavy", true), ("light", false))).Passed.ShouldBeTrue();
        LlmJudgeGrader.Aggregate(rubric with { Threshold = 0.8 }, Verdict(("heavy", true), ("light", false))).Passed.ShouldBeFalse();
    }

    [Fact]
    public void The_float_boundary_is_safe()
    {
        // 2 of 3 equal weights = 0.6666…; a 2/3 threshold must PASS despite representation error.
        var rubric = Rubric(("a", null), ("b", null), ("c", null)) with { Threshold = 2.0 / 3.0 };

        LlmJudgeGrader.Aggregate(rubric, Verdict(("a", true), ("b", true), ("c", false))).Passed.ShouldBeTrue();
    }

    [Fact]
    public void Negative_weights_clamp_and_an_all_zero_rubric_fails_closed()
    {
        var negatives = Rubric(("a", -5.0), ("b", 1.0));
        LlmJudgeGrader.Aggregate(negatives, Verdict(("a", false), ("b", true))).Passed.ShouldBeTrue("the negative weight clamps to 0 — only b decides");

        var allZero = Rubric(("a", 0.0), ("b", 0.0));
        var grade = LlmJudgeGrader.Aggregate(allZero, Verdict(("a", true), ("b", true)));
        grade.Passed.ShouldBeFalse("a contract that weighs nothing decides nothing — fail-closed, never a silent pass");
        grade.Detail.ShouldContain("no-effective-weight");
    }

    // ─── LlmJudgeGrader fail-closed guards + the judge seam ──────────────────

    [Fact]
    public async Task The_judge_grader_fails_closed_on_every_missing_precondition()
    {
        var grader = new LlmJudgeGrader(new FakeScopeFactory(new FakeJudge()));
        var spec = JudgeSpec("report.md");

        (await grader.GradeAsync(Context(spec, workspace: null), CancellationToken.None)).Detail.ShouldBe("no-workspace");
        (await grader.GradeAsync(Context(spec) with { TeamId = null }, CancellationToken.None)).Detail.ShouldContain("no-team");
        (await grader.GradeAsync(Context(spec with { Rubric = null }), CancellationToken.None)).Detail.ShouldBe("no-rubric");
        (await grader.GradeAsync(Context(spec with { Command = Array.Empty<string>() }), CancellationToken.None)).Detail.ShouldBe("no-artifact-paths");
        (await grader.GradeAsync(Context(spec), CancellationToken.None)).Detail.ShouldBe("artifact-missing: report.md", customMessage: "the deliverable does not exist — fail-closed before any judge call");
    }

    [Fact]
    public async Task A_failed_judge_is_a_grade_error_so_the_revise_loop_never_burns_a_round_on_it()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "report.md"), "content");
        var grader = new LlmJudgeGrader(new FakeScopeFactory(new FakeJudge { Verdict = RubricJudgeVerdict.JudgeFailed("no-judge-model: pool empty") }));

        var grade = await grader.GradeAsync(Context(JudgeSpec("report.md")), CancellationToken.None);

        grade.Passed.ShouldBeFalse();
        grade.Detail.ShouldStartWith("grade-error:", customMessage: "a broken judge is INFRA — the S6 loop's grade-error exclusion keys on this prefix");
    }

    [Fact]
    public async Task A_real_verdict_reaches_the_aggregate_with_the_deliverables_read()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "report.md"), "the report body");
        var judge = new FakeJudge { Verdict = new RubricJudgeVerdict { Criteria = new[] { new RubricCriterionVerdict { Id = "a", Met = true, Evidence = "found it" } } } };
        var grader = new LlmJudgeGrader(new FakeScopeFactory(judge));

        var grade = await grader.GradeAsync(Context(JudgeSpec("report.md")), CancellationToken.None);

        grade.Passed.ShouldBeTrue();
        judge.LastArtifact.ShouldContain("=== report.md ===", customMessage: "the judged artifact is the deliverable content under its path header");
        judge.LastArtifact.ShouldContain("the report body");
    }

    // ─── CitationsResolveGrader ──────────────────────────────────────────────

    [Fact]
    public void Citation_extraction_reads_markdown_links_and_images()
    {
        var citations = CitationsResolveGrader.ExtractCitations(
            "See [the spec](docs/spec.md) and [site](https://example.com/a \"title\") plus ![chart](img/chart.png).");

        citations.ShouldBe(new[] { "docs/spec.md", "https://example.com/a", "img/chart.png" });
    }

    [Theory]
    [InlineData("https://example.com/paper", null)]              // absolute https with host → resolves
    [InlineData("http://example.com", null)]                     // http too
    [InlineData("#section-2", null)]                             // self-anchor → accepted (documented bound)
    [InlineData("mailto:a@b.c", "unsupported-scheme:mailto")]    // non-http scheme → fail
    [InlineData("ftp://example.com/x", "unsupported-scheme:ftp")]
    public void Citation_targets_resolve_by_scheme(string target, string? expectedFailure) =>
        CitationsResolveGrader.ResolveCitation("/nonexistent-root", "report.md", target).ShouldBe(expectedFailure);

    [Fact]
    public void Relative_citations_resolve_against_the_citing_files_directory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "docs", "source.md"), "src");

        var root = Path.GetFullPath(_root);

        CitationsResolveGrader.ResolveCitation(root, "docs/report.md", "source.md").ShouldBeNull("sibling of the citing file");
        CitationsResolveGrader.ResolveCitation(root, "docs/report.md", "source.md#heading").ShouldBeNull("the #fragment is stripped before the existence check");
        CitationsResolveGrader.ResolveCitation(root, "report.md", "docs/source.md").ShouldBeNull("root-level citing file");
        CitationsResolveGrader.ResolveCitation(root, "report.md", "missing.md").ShouldBe("file-not-found-in-workspace");
        CitationsResolveGrader.ResolveCitation(root, "docs/report.md", "../../etc/passwd").ShouldBe("file-not-found-in-workspace", "an escape is clamped by the shared guard — never resolvable");
    }

    [Fact]
    public async Task The_citations_grade_passes_on_resolving_files_and_names_the_first_broken_target()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        await File.WriteAllTextAsync(Path.Combine(_root, "docs", "source.md"), "src");
        await File.WriteAllTextAsync(Path.Combine(_root, "report.md"), "See [src](docs/source.md) and [web](https://example.com/p).");
        await File.WriteAllTextAsync(Path.Combine(_root, "broken.md"), "See [gone](docs/missing.md).");
        await File.WriteAllTextAsync(Path.Combine(_root, "bare.md"), "no citations at all");

        var grader = new CitationsResolveGrader();

        (await grader.GradeAsync(Context(Spec(BenchmarkGradingKind.CitationsResolve, "report.md")), CancellationToken.None)).Passed.ShouldBeTrue();

        var broken = await grader.GradeAsync(Context(Spec(BenchmarkGradingKind.CitationsResolve, "report.md", "broken.md")), CancellationToken.None);
        broken.Passed.ShouldBeFalse();
        broken.Detail.ShouldContain("broken.md → docs/missing.md");

        var bare = await grader.GradeAsync(Context(Spec(BenchmarkGradingKind.CitationsResolve, "bare.md")), CancellationToken.None);
        bare.Passed.ShouldBeFalse();
        bare.Detail.ShouldContain("citations-missing", customMessage: "a research deliverable with zero citations fails — the check exists to demand sourcing");
    }

    // ─── ArtifactSchemaGrader ────────────────────────────────────────────────

    [Fact]
    public async Task The_schema_grade_validates_each_deliverable_and_names_the_violations()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "good.json"), """{ "name": "x", "count": 2 }""");
        await File.WriteAllTextAsync(Path.Combine(_root, "bad.json"), """{ "count": "two" }""");
        await File.WriteAllTextAsync(Path.Combine(_root, "notjson.txt"), "prose");

        var schema = JsonDocument.Parse("""{ "type": "object", "required": ["name"], "properties": { "name": { "type": "string" }, "count": { "type": "integer" } } }""").RootElement.Clone();
        var grader = new ArtifactSchemaGrader();

        (await grader.GradeAsync(Context(SchemaSpec(schema, "good.json")), CancellationToken.None)).Passed.ShouldBeTrue();

        var bad = await grader.GradeAsync(Context(SchemaSpec(schema, "bad.json")), CancellationToken.None);
        bad.Passed.ShouldBeFalse();
        bad.Detail.ShouldContain("schema-violations: bad.json");
        bad.Detail.ShouldContain("name", customMessage: "the missing required property is named");

        (await grader.GradeAsync(Context(SchemaSpec(schema, "notjson.txt")), CancellationToken.None)).Detail.ShouldStartWith("artifact-not-json");
        (await grader.GradeAsync(Context(Spec(BenchmarkGradingKind.ArtifactSchema, "good.json")), CancellationToken.None)).Detail.ShouldBe("no-schema", customMessage: "a schema check without a schema is fail-closed, never vacuous");
    }

    // ─── The shared guard's read cap ─────────────────────────────────────────

    [Fact]
    public void Over_cap_content_truncates_with_a_visible_marker()
    {
        File.WriteAllText(Path.Combine(_root, "big.txt"), new string('x', 200));

        WorkspaceArtifactGuard.TryReadWithin(Path.GetFullPath(_root), "big.txt", maxBytes: 50, out var content, out _).ShouldBeTrue();

        content.ShouldContain("[... truncated for grading ...]");
        content.Length.ShouldBeLessThan(120);
    }

    // ─── fixtures ────────────────────────────────────────────────────────────

    private static AcceptanceRubric Rubric(params (string Id, double? Weight)[] criteria) => new()
    {
        Criteria = criteria.Select(c => new AcceptanceRubricCriterion { Id = c.Id, Requirement = $"requirement {c.Id}", Weight = c.Weight }).ToList(),
    };

    private static RubricJudgeVerdict Verdict(params (string Id, bool Met)[] verdicts) => new()
    {
        Criteria = verdicts.Select(v => new RubricCriterionVerdict { Id = v.Id, Met = v.Met, Evidence = $"evidence {v.Id}" }).ToList(),
    };

    private static SupervisorAcceptanceSpec Spec(BenchmarkGradingKind kind, params string[] paths) => new() { Command = paths, Kind = kind };

    private SupervisorAcceptanceSpec JudgeSpec(params string[] paths) =>
        Spec(BenchmarkGradingKind.LlmJudge, paths) with { Rubric = Rubric(("a", null)) };

    private static SupervisorAcceptanceSpec SchemaSpec(JsonElement schema, params string[] paths) =>
        Spec(BenchmarkGradingKind.ArtifactSchema, paths) with { Schema = schema };

    private BenchmarkGradingContext Context(SupervisorAcceptanceSpec spec, string? workspace = "default") =>
        BenchmarkGradingContext.ForAcceptance(spec, Guid.NewGuid(), 30, workspace == "default" ? _root : workspace!, new NoRunner()) with
        {
            WorkspaceDirectory = workspace == "default" ? _root : workspace,
        };

    private sealed class NoRunner : ISandboxRunner
    {
        public string Kind => "none";
        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken) => throw new NotSupportedException("the non-coding oracles never exec");
    }

    private sealed class FakeJudge : IRubricJudge
    {
        public RubricJudgeVerdict Verdict { get; set; } = new() { Criteria = Array.Empty<RubricCriterionVerdict>() };
        public string? LastArtifact { get; private set; }

        public Task<RubricJudgeVerdict> JudgeAsync(AcceptanceRubric rubric, string artifact, string? goal, Guid teamId, CancellationToken cancellationToken)
        {
            LastArtifact = artifact;
            return Task.FromResult(Verdict);
        }
    }

    private sealed class FakeScopeFactory : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory, Microsoft.Extensions.DependencyInjection.IServiceScope, IServiceProvider
    {
        private readonly IRubricJudge _judge;
        public FakeScopeFactory(IRubricJudge judge) { _judge = judge; }

        public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) => serviceType == typeof(IRubricJudge) ? _judge : null;
        public void Dispose() { }
    }
}
