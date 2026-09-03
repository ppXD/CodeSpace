using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Retention;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Artifacts;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;
using Shouldly;
using System.Text;
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

    [Fact]
    public void An_intermediate_directory_symlink_cannot_escape_the_workspace()
    {
        if (OperatingSystem.IsWindows()) return;

        using var root = new TempDir();
        using var outside = new TempDir();
        File.WriteAllText(Path.Combine(outside.Path, "secret.txt"), "outside");
        Directory.CreateSymbolicLink(Path.Combine(root.Path, "linked"), outside.Path);

        WorkspaceArtifactGuard.ExistsWithin(root.Path, "linked/secret.txt").ShouldBeFalse(
            "checking only the leaf misses an escaping symlink in a parent component");
    }

    [Fact]
    public async Task A_parent_symlink_swap_after_file_admission_cannot_redirect_streaming_capture()
    {
        if (OperatingSystem.IsWindows()) return;

        using var root = new TempDir();
        using var outside = new TempDir();
        var docs = Path.Combine(root.Path, "docs");
        var admitted = Path.Combine(root.Path, "admitted-docs");
        Directory.CreateDirectory(docs);
        File.WriteAllBytes(Path.Combine(docs, "report.md"), "inside!"u8.ToArray());
        File.WriteAllBytes(Path.Combine(outside.Path, "report.md"), "secret!"u8.ToArray());
        var retention = new GatedRetentionWriter();
        var capture = new ArtifactManifestStore(null!, retention, new CapturingLogger()).CaptureDeclaredAsync(
            TaskDeclaring("docs/report.md"), root.Path, Guid.NewGuid(), null, Guid.NewGuid(), 1, CancellationToken.None);

        await retention.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        Directory.Move(docs, admitted);
        Directory.CreateSymbolicLink(docs, outside.Path);
        retention.Release();

        await Should.ThrowAsync<CaptureProbeCompletedException>(capture);
        retention.Observed.Count.ShouldBe(2, "the real source is reopened for admission and placement");
        retention.Observed.ShouldAllBe(bytes => Encoding.UTF8.GetString(bytes) == "inside!",
            "both passes must read the already-admitted file handle, never a workspace path redirected after containment validation");
    }

    /// <summary>
    /// Every refusal stays a DISTINCT member for the bounded byte-reader's callers. The manifest store now uses the
    /// file resolver instead, so a real file never reaches its historical over-cap arm.
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
    /// fact, nothing else. Streaming capture has no byte-array cap arm; a large real file takes the streaming path.
    /// <para>The null collaborators are unreachable BY PROOF, not by luck: no declared path here yields bytes, so
    /// neither the CAS declaring write nor the manifest upsert runs, so the db and retention writer are untouched on
    /// every arm this drives.</para>
    /// </summary>
    [Fact]
    public async Task Every_non_file_refusal_is_logged_without_touching_storage()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "docs"));
        var logger = new CapturingLogger();

        var captured = await new ArtifactManifestStore(null!, null!, logger).CaptureDeclaredAsync(
            TaskDeclaring("never-written.md", "docs"), dir.Path, Guid.NewGuid(), null, Guid.NewGuid(), 1, CancellationToken.None);

        captured.ShouldBe(0, "no declared path here is capturable, so the store reached neither the CAS nor the database");
        logger.Warnings.Count.ShouldBe(2, "every skipped deliverable gets its own line — a skip nobody logged is a loss nobody can see");

        logger.Warnings[0].ShouldContain(nameof(WorkspaceArtifactReadFailure.Missing));

        logger.Warnings[1].ShouldContain(nameof(WorkspaceArtifactReadFailure.NotAFile));
    }

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

    private sealed class GatedRetentionWriter : IArtifactStreamRetentionWriter
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public List<byte[]> Observed { get; } = new();

        public void Release() => _released.TrySetResult();

        public async Task<ArtifactStreamRetentionWrite> PutDeclaredAsync(ArtifactStreamRetentionWriteRequest request, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
            Observed.Add(await ReadAsync(request.Artifact.Source, cancellationToken));
            Observed.Add(await ReadAsync(request.Artifact.Source, cancellationToken));
            throw new CaptureProbeCompletedException();
        }

        private static async Task<byte[]> ReadAsync(IArtifactWriteSource source, CancellationToken cancellationToken)
        {
            await using var stream = await source.OpenReadAsync(cancellationToken);
            var bytes = new byte[checked((int)source.LengthBytes)];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            return bytes;
        }
    }

    private sealed class CaptureProbeCompletedException : Exception;

    // ── C2: the UNDECLARED scratch walk — its pinned limits, its allowlist, its determinism ──────────────

    /// <summary>
    /// Rule 8: these four literals decide what a repo-less run's world keeps. Raising the caps silently is how an
    /// artifact store fills up; lowering them silently is how a deliverable disappears. Widening the allowlist
    /// silently is how a walk nobody asked for starts lifting binaries. Every change stays a visible decision.
    /// </summary>
    [Fact]
    public void The_undeclared_walks_limits_and_allowlist_are_pinned()
    {
        ArtifactManifestStore.MaxUndeclaredCaptureFiles.ShouldBe(32);
        ArtifactManifestStore.MaxUndeclaredCaptureBytes.ShouldBe(8L * 1024 * 1024);
        ArtifactManifestStore.MaxUndeclaredScanEntries.ShouldBe(20000);
        ArtifactManifestStore.MaxUndeclaredScanSeconds.ShouldBe(10);

        ArtifactManifestStore.CapturableUndeclaredExtensions.ShouldBe(
            new[] { ".csv", ".docx", ".html", ".json", ".jsonl", ".md", ".mmd", ".pdf", ".png", ".puml", ".rst", ".svg", ".tsv", ".txt", ".xlsx", ".xml", ".yaml", ".yml" },
            customMessage: "text plus the document formats KindFor already types — an agent's report.pdf is exactly what this walk exists to keep");

        ArtifactManifestStore.SkippedWalkDirectories.ShouldBe(
            new[] { ".git", "bin", "dist", "node_modules", "obj", "target", "vendor" },
            customMessage: "build outputs and dependency trees the agent did not author — descending them exhausts the budget and crowds out the real deliverable");
    }

    /// <summary>
    /// Every extension the store can already TYPE must be one the walk can take. A gap here is not cosmetic: an
    /// undeclared <c>report.pdf</c> would be refused, the walk would capture nothing, and an empty world grades as
    /// "the agent produced nothing" — a GENUINE verdict that buys retries for a file that was sitting right there.
    /// </summary>
    [Theory]
    [InlineData("report.pdf")]
    [InlineData("summary.docx")]
    [InlineData("data/book.xlsx")]
    [InlineData("chart.png")]
    public void Every_known_document_kind_is_capturable_by_the_walk(string path)
    {
        ArtifactManifestStore.KindFor(path).ShouldNotBe(ArtifactManifestKind.Other, "this extension is one the store types");
        ArtifactManifestStore.IsCapturableUndeclared(path).ShouldBeTrue("a typed document the walk refuses is a deliverable lost to a retry loop");
    }

    [Theory]
    [InlineData("report.md", true)]
    [InlineData("notes/findings.txt", true)]
    [InlineData("data/rows.CSV", true)]                  // extension casing never changes the verdict
    [InlineData("report.pdf", true)]                     // a report an agent wrote as a PDF is still the deliverable
    [InlineData("build/agent.bin", false)]               // not a text/document extension
    [InlineData("archive.tar.gz", false)]
    [InlineData("report", false)]                        // no extension at all
    [InlineData(".env", false)]                          // a dotfile is never lifted — this is the credential case
    [InlineData(".claude/settings.json", false)]         // …nor one nested under a harness's own dot-directory
    [InlineData("docs/.secret.md", false)]               // …at any depth
    public void The_walk_takes_only_non_dotfile_text_documents(string relativePath, bool capturable)
    {
        ArtifactManifestStore.IsCapturableUndeclared(relativePath).ShouldBe(capturable);
    }

    /// <summary>
    /// A dependency tree costs ONE entry, not everything beneath it. Without the skip the walk pays for thousands of
    /// files the agent never authored, and the scan/byte budget is spent before it reaches the one report it exists
    /// to keep — the deliverable is crowded out by node_modules.
    /// </summary>
    [Fact]
    public void The_walk_never_descends_a_build_or_dependency_tree()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "node_modules", "left-pad"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "obj", "Debug"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "docs"));
        File.WriteAllText(Path.Combine(dir.Path, "node_modules", "left-pad", "index.json"), "{}");
        File.WriteAllText(Path.Combine(dir.Path, "obj", "Debug", "build.json"), "{}");
        File.WriteAllText(Path.Combine(dir.Path, "docs", "report.md"), "# the deliverable");

        ArtifactManifestStore.Walk(dir.Path).Select(f => f.Path).ShouldBe(new[] { "docs/report.md" });
    }

    /// <summary>A walk is a capture step, never a reason a run's completion hangs — an already-cancelled token stops it before it reads a tree.</summary>
    [Fact]
    public void The_walk_honours_cancellation()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "report.md"), "x");

        ArtifactManifestStore.Walk(dir.Path, new CancellationToken(canceled: true)).ShouldBeEmpty();
    }

    /// <summary>
    /// The walk's ORDER is the walk's fairness: a bounded selection that took a different subset each run would make
    /// "what did this attempt keep?" unanswerable. Ordinal by relative path, and symlinks never enter the list.
    /// </summary>
    [Fact]
    public void The_walk_is_ordinally_deterministic_and_never_descends_a_symlink()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "notes"));
        File.WriteAllText(Path.Combine(dir.Path, "zebra.md"), "z");
        File.WriteAllText(Path.Combine(dir.Path, "alpha.md"), "aa");
        File.WriteAllText(Path.Combine(dir.Path, "notes", "beta.txt"), "bbb");

        var walked = ArtifactManifestStore.Walk(dir.Path);

        walked.Select(f => f.Path).ShouldBe(new[] { "alpha.md", "notes/beta.txt", "zebra.md" });
        walked.Single(f => f.Path == "notes/beta.txt").LengthBytes.ShouldBe(3, "the byte budget is spent against real lengths");

        if (OperatingSystem.IsWindows()) return;

        using var outside = new TempDir();
        File.WriteAllText(Path.Combine(outside.Path, "stolen.md"), "not yours");
        Directory.CreateSymbolicLink(Path.Combine(dir.Path, "escape"), outside.Path);

        ArtifactManifestStore.Walk(dir.Path).Select(f => f.Path)
            .ShouldNotContain("escape/stolen.md", "the recursion never descends a reparse point — and the capture guard re-clamps every component besides");
    }

    /// <summary>
    /// C2's shortfall half: the walk's own ceiling is the one loss nothing else in the capture facts could reveal —
    /// a run that captured three files because the cap stopped it looks identical, without this, to a world that
    /// held exactly three.
    /// </summary>
    [Fact]
    public void The_capture_facts_carry_the_scratch_walks_pair_and_a_walk_only_run_is_not_empty()
    {
        var facts = JsonDocument.Parse(AgentRunExecutor.CaptureFactsOf(
            new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", UndeclaredArtifactCount = 3, UncapturedScratchFileCount = 41 },
            NothingDeclared)).RootElement;

        facts.GetProperty("undeclaredArtifacts").GetInt32().ShouldBe(3);
        facts.GetProperty("uncapturedScratchFiles").GetInt32().ShouldBe(41, "an over-limit capture must be VISIBLE, not silent");
        facts.GetProperty("empty").GetBoolean().ShouldBeFalse("a run whose only capture was the walk still captured something — recording it empty would be the same lie the typed-only case used to tell");
    }

    /// <summary>
    /// The ONE kind rule the whole repo-less lane turns on (the executor's scratch grade, the supervisor fold's
    /// captured grade, and the declared-path derivation all read it here). A TestsPass argv in a directory of
    /// captured documents is a category error — a bare <c>exit 0</c> would pass vacuously — so it stays fail-closed.
    /// </summary>
    [Theory]
    [InlineData(BenchmarkGradingKind.ArtifactPresent, true)]
    [InlineData(BenchmarkGradingKind.LlmJudge, true)]
    [InlineData(BenchmarkGradingKind.CitationsResolve, true)]
    [InlineData(BenchmarkGradingKind.ArtifactSchema, true)]
    [InlineData(BenchmarkGradingKind.TestsPass, false)]
    [InlineData(null, false)]
    public void Only_a_deliverable_shaped_kind_grades_from_files(BenchmarkGradingKind? kind, bool fromDeliverables)
    {
        AgentAcceptanceContract.GradesFromDeliverables(new SupervisorAcceptanceSpec { Command = new[] { "x" }, Kind = kind }).ShouldBe(fromDeliverables);
        AgentAcceptanceContract.GradesFromDeliverables(null).ShouldBeFalse("no contract grades from nothing");
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
