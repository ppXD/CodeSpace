using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: DC-4's typed-artifact capture seams — WHICH paths a task declares capturable (only a non-TestsPass
/// acceptance's Command list is paths; TestsPass carries an argv), the extension→kind/MIME routing (honest-default
/// Other/octet-stream), the byte-read guard's skip-over-cap posture and its TYPED refusals (a clipped dataset is a
/// lie; absence is honest), and BOTH halves of the capture promise — the declared list it states going in, and the
/// facts naming typed captures coming out (without which a typed-only capture committed <c>empty: true</c>, and a run
/// that owed three deliverables and took none committed the same thing as a run that owed none). Both halves are read
/// off the SAME acceptance, so an attempt can never commit facts that contradict the promise it opened.
/// </summary>
[Trait("Category", "Unit")]
public class ArtifactManifestCaptureTests
{
    [Fact]
    public void Only_a_non_tests_pass_acceptance_declares_capturable_paths()
    {
        DeclaredOf(kind: BenchmarkGradingKind.ArtifactPresent, "docs/report.md", "docs/report.md").ShouldBe(new[] { "docs/report.md" },
            customMessage: "paths dedupe; a declared deliverable list is paths, not an argv");
        DeclaredOf(kind: BenchmarkGradingKind.TestsPass, "dotnet", "test").ShouldBeEmpty("a TestsPass Command is an ARGV — capturing 'dotnet' as an artifact would be nonsense");
        DeclaredOf(kind: null, "dotnet", "test").ShouldBeEmpty("an absent kind defaults to TestsPass");
        ArtifactManifestStore.DeclaredDeliverablePaths(new AgentTask { Goal = "g", Harness = "codex-cli" }).ShouldBeEmpty("no acceptance declares nothing");
    }

    [Theory]
    [InlineData("docs/report.md", ArtifactManifestKind.Document, "text/markdown")]
    [InlineData("arch/flow.svg", ArtifactManifestKind.Diagram, "image/svg+xml")]
    [InlineData("data/rows.csv", ArtifactManifestKind.Dataset, "text/csv")]
    [InlineData("bin/tool.exe", ArtifactManifestKind.Other, "application/octet-stream")]
    [InlineData("Chart.MMD", ArtifactManifestKind.Diagram, "text/plain")]   // extension casing never changes the verdict
    public void The_extension_routes_kind_and_mime(string path, ArtifactManifestKind kind, string mime)
    {
        ArtifactManifestStore.KindFor(path).ShouldBe(kind);
        ArtifactManifestStore.ContentTypeFor(path).ShouldBe(mime);
    }

    [Fact]
    public void The_byte_guard_skips_an_over_cap_file_never_truncates()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Path, "big.csv"), new byte[64]);

        WorkspaceArtifactGuard.TryReadBytesWithin(dir.Path, "big.csv", maxBytes: 32, out _, out var failure).ShouldBeFalse();
        failure.ShouldBe(WorkspaceArtifactReadFailure.OverCap, "a captured artifact's bytes ARE the deliverable — a silent clip is a lie, absence is honest");

        WorkspaceArtifactGuard.TryReadBytesWithin(dir.Path, "big.csv", maxBytes: 64, out var bytes, out _).ShouldBeTrue();
        bytes.Length.ShouldBe(64);
    }

    /// <summary>
    /// Every refusal is a DISTINCT member, because the capture branches on them: only the over-cap arm is bytes the run
    /// produced and the capture failed to take, and only that arm is recorded as a known-missing span. A shared string
    /// makes that distinction a substring match nobody performs.
    /// </summary>
    [Fact]
    public void Each_guard_refusal_is_its_own_enum_member()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Path, "big.csv"), new byte[64]);
        Directory.CreateDirectory(Path.Combine(dir.Path, "docs"));

        Refusal(dir, "../outside.txt", maxBytes: 1024).ShouldBe(WorkspaceArtifactReadFailure.Missing, "a ../ escape reads as missing — the containment guard never reaches outside the workspace");
        Refusal(dir, "never-written.md", maxBytes: 1024).ShouldBe(WorkspaceArtifactReadFailure.Missing);
        Refusal(dir, "docs", maxBytes: 1024).ShouldBe(WorkspaceArtifactReadFailure.NotAFile, "a directory is not readable content");
        Refusal(dir, "big.csv", maxBytes: 32).ShouldBe(WorkspaceArtifactReadFailure.OverCap);
    }

    [Fact]
    public void The_capture_facts_name_typed_artifacts_and_an_all_empty_run_stays_empty()
    {
        var typedOnly = AgentRunExecutor.CaptureFactsOf(
            new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", CapturedArtifactCount = 2 },
            TaskDeclaring("docs/report.md", "data/rows.csv"));
        var facts = JsonDocument.Parse(typedOnly).RootElement;

        facts.GetProperty("typedArtifacts").GetInt32().ShouldBe(2);
        facts.GetProperty("empty").GetBoolean().ShouldBeFalse("a typed-only capture is a REAL capture — recording it as empty was the live lie this closes");

        JsonDocument.Parse(AgentRunExecutor.CaptureFactsOf(new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" }, NothingDeclared))
            .RootElement.GetProperty("empty").GetBoolean().ShouldBeTrue("nothing captured is still an explicit, confirmed empty");
    }

    /// <summary>
    /// The difference the promise exists to make visible: a run that OWED deliverables is never the confirmed empty a
    /// run that owed none is, however few it took. Three declared and one taken is a shortfall; three declared and none
    /// taken is a total one — and both used to serialise identically to "nothing happened here".
    /// </summary>
    [Theory]
    [InlineData(3, 1)]
    [InlineData(3, 0)]
    public void A_run_that_declared_deliverables_is_never_a_confirmed_empty(int declared, int captured)
    {
        var facts = JsonDocument.Parse(AgentRunExecutor.CaptureFactsOf(
            new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", CapturedArtifactCount = captured },
            TaskDeclaring(Enumerable.Range(1, declared).Select(ordinal => $"docs/d{ordinal}.md").ToArray()))).RootElement;

        facts.GetProperty("declaredDeliverables").GetInt32().ShouldBe(declared, "the facts carry what was owed, or the shortfall has nothing to be measured against");
        facts.GetProperty("typedArtifacts").GetInt32().ShouldBe(captured);
        facts.GetProperty("empty").GetBoolean().ShouldBeFalse("a promise that owed three deliverables did not confirm an empty capture — it fell short of one");
    }

    /// <summary>
    /// The two halves of ONE promise are read off the SAME declaration, so no attempt can commit facts that contradict
    /// what it promised. The path this pins is the re-attach: the live workspace died with the worker, so the
    /// declared-deliverable capture never runs and the RESULT carries nothing about deliverables — while the promise
    /// this attempt opened names three files. A count derived from the capture PASS answers 0 there, which reads as
    /// "nothing was ever declared" beside a promise that says three were owed.
    /// </summary>
    [Fact]
    public void A_promise_and_its_facts_never_disagree_about_what_was_owed()
    {
        var task = TaskDeclaring("docs/report.md", "data/rows.csv", "big.csv");
        var noCapturePass = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" };

        var owed = JsonDocument.Parse(AgentRunExecutor.CaptureExpectationsOf(task)!).RootElement.GetProperty("deliverables").GetArrayLength();
        var facts = JsonDocument.Parse(AgentRunExecutor.CaptureFactsOf(noCapturePass, task)).RootElement;

        facts.GetProperty("declaredDeliverables").GetInt32().ShouldBe(owed, "an attempt whose promise names three deliverables cannot commit facts saying none were owed");
        facts.GetProperty("typedArtifacts").GetInt32().ShouldBe(0, "no typed artifact was captured on this path — that IS the shortfall, and it stays visible");
        facts.GetProperty("empty").GetBoolean().ShouldBeFalse("a run that owed three deliverables and took none fell short of a capture; it did not confirm an empty one");
    }

    /// <summary>
    /// The promise's intent-time half: the declared list rides the capture window so the commit's counts have something
    /// to be short OF. An acceptance that declares no paths states null — the honest "nothing was owed", not a silence
    /// a total loss could hide behind.
    /// </summary>
    [Fact]
    public void The_capture_promise_states_the_declared_deliverable_list()
    {
        var expectations = AgentRunExecutor.CaptureExpectationsOf(new AgentTask
        {
            Goal = "g", Harness = "codex-cli",
            Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "docs/report.md", "data/rows.csv" }, Kind = BenchmarkGradingKind.ArtifactPresent },
        });

        JsonDocument.Parse(expectations!).RootElement.GetProperty("deliverables").EnumerateArray().Select(path => path.GetString())
            .ShouldBe(new[] { "docs/report.md", "data/rows.csv" });

        AgentRunExecutor.CaptureExpectationsOf(new AgentTask { Goal = "g", Harness = "codex-cli" })
            .ShouldBeNull("an acceptance declaring no paths owed nothing — that is a statement, not a shortfall");
    }

    /// <summary>
    /// On the Missing and NotAFile arms this warning is the ONLY account a lost deliverable ever gets — no gap row, no
    /// fact, nothing else. Naming the per-file capture cap on all three arms points two of them at a bound that had no
    /// part in the refusal: the file the agent never wrote is not a file that was too big, and an operator sent to
    /// check a size limit is an operator not looking at the acceptance list.
    /// <para>The null collaborators are unreachable BY PROOF, not by luck: no declared path here yields bytes, so
    /// neither the CAS declaring write nor the manifest upsert runs, and a STANDALONE attempt (no workflow run) has
    /// nowhere to record a gap — so the db, the retention writer and the completeness writer are untouched on every
    /// arm this drives.</para>
    /// </summary>
    [Fact]
    public async Task The_skip_warning_blames_the_cap_only_where_a_cap_was_the_cause()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "docs"));
        using (var big = File.Create(Path.Combine(dir.Path, "big.csv"))) big.SetLength(ArtifactManifestStore.MaxArtifactBytes + 1);

        var logger = new CapturingLogger();

        var captured = await new ArtifactManifestStore(null!, null!, null!, logger).CaptureDeclaredAsync(
            TaskDeclaring("never-written.md", "docs", "big.csv"), dir.Path, Guid.NewGuid(), null, Guid.NewGuid(), 1, CancellationToken.None);

        captured.ShouldBe(0, "no declared path here is capturable, so the store reached neither the CAS nor the database");
        logger.Warnings.Count.ShouldBe(3, "every skipped deliverable gets its own line — a skip nobody logged is a loss nobody can see");

        logger.Warnings[0].ShouldContain(nameof(WorkspaceArtifactReadFailure.Missing));
        logger.Warnings[0].ShouldNotContain(Cap, customMessage: "a deliverable the agent never wrote was not stopped by a bound");

        logger.Warnings[1].ShouldContain(nameof(WorkspaceArtifactReadFailure.NotAFile));
        logger.Warnings[1].ShouldNotContain(Cap, customMessage: "a directory is not readable content — no bound was consulted");

        logger.Warnings[2].ShouldContain(nameof(WorkspaceArtifactReadFailure.OverCap));
        logger.Warnings[2].ShouldContain(Cap, customMessage: "the ONE arm a cap decided names the cap it hit");
    }

    /// <summary>The per-file cap as it renders into a log line — the token that must appear on exactly one arm.</summary>
    private static string Cap => ArtifactManifestStore.MaxArtifactBytes.ToString();

    private static WorkspaceArtifactReadFailure? Refusal(TempDir dir, string path, long maxBytes)
    {
        WorkspaceArtifactGuard.TryReadBytesWithin(dir.Path, path, maxBytes, out _, out var failure).ShouldBeFalse();
        return failure;
    }

    private static AgentTask NothingDeclared { get; } = new() { Goal = "g", Harness = "codex-cli" };

    private static AgentTask TaskDeclaring(params string[] paths) => new()
    {
        Goal = "g", Harness = "codex-cli",
        Acceptance = new SupervisorAcceptanceSpec { Command = paths, Kind = BenchmarkGradingKind.ArtifactPresent },
    };

    private static IReadOnlyList<string> DeclaredOf(BenchmarkGradingKind? kind, params string[] command) =>
        ArtifactManifestStore.DeclaredDeliverablePaths(new AgentTask
        {
            Goal = "g", Harness = "codex-cli",
            Acceptance = new SupervisorAcceptanceSpec { Command = command, Kind = kind },
        });

    private sealed class CapturingLogger : ILogger<ArtifactManifestStore>
    {
        public List<string> Warnings { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs-artifact-capture-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
