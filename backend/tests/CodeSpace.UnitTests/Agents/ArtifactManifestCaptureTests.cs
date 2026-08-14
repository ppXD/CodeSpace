using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: DC-4's typed-artifact capture seams — WHICH paths a task declares capturable (only a non-TestsPass
/// acceptance's Command list is paths; TestsPass carries an argv), the extension→kind/MIME routing (honest-default
/// Other/octet-stream), the byte-read guard's skip-over-cap posture (a clipped dataset is a lie; absence is
/// honest), and the capture promise's facts naming typed captures (without it a typed-only capture committed
/// <c>empty: true</c> — a live lie).
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

        WorkspaceArtifactGuard.TryReadBytesWithin(dir.Path, "big.csv", maxBytes: 32, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
        error!.ShouldContain("over-cap", customMessage: "a captured artifact's bytes ARE the deliverable — a silent clip is a lie, absence is honest");

        WorkspaceArtifactGuard.TryReadBytesWithin(dir.Path, "big.csv", maxBytes: 64, out var bytes, out _).ShouldBeTrue();
        bytes.Length.ShouldBe(64);
    }

    [Fact]
    public void The_byte_guard_fails_closed_on_an_escaping_path()
    {
        using var dir = new TempDir();

        WorkspaceArtifactGuard.TryReadBytesWithin(dir.Path, "../outside.txt", maxBytes: 1024, out _, out var error).ShouldBeFalse();
        error!.ShouldContain("artifact-missing");
    }

    [Fact]
    public void The_capture_facts_name_typed_artifacts_and_an_all_empty_run_stays_empty()
    {
        var typedOnly = AgentRunExecutor.CaptureFactsOf(new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", CapturedArtifactCount = 2 });
        var facts = JsonDocument.Parse(typedOnly).RootElement;

        facts.GetProperty("typedArtifacts").GetInt32().ShouldBe(2);
        facts.GetProperty("empty").GetBoolean().ShouldBeFalse("a typed-only capture is a REAL capture — recording it as empty was the live lie this closes");

        JsonDocument.Parse(AgentRunExecutor.CaptureFactsOf(new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" }))
            .RootElement.GetProperty("empty").GetBoolean().ShouldBeTrue("nothing captured is still an explicit, confirmed empty");
    }

    private static IReadOnlyList<string> DeclaredOf(BenchmarkGradingKind? kind, params string[] command) =>
        ArtifactManifestStore.DeclaredDeliverablePaths(new AgentTask
        {
            Goal = "g", Harness = "codex-cli",
            Acceptance = new SupervisorAcceptanceSpec { Command = command, Kind = kind },
        });

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
