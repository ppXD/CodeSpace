using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System.Text;

namespace CodeSpace.UnitTests.Supervisor;

/// <summary>
/// 🟢 Unit: C2's world-rebuild for a REPO-LESS unit — the real <see cref="SupervisorAcceptanceGrader"/> driving a
/// real grader registry, with only the store seams faked. The supervisor's terminal fold runs after the producing
/// worker's scratch directory is gone and, on a multi-worker deployment, on a host that never held it, so the unit's
/// durable <c>artifact_manifest</c> rows are the only sound world. These pin that the rebuild is faithful (exact
/// bytes, own logical paths, nested paths), that the verdict comes from the SAME per-kind oracle every other lane
/// resolves, that a unit which captured nothing gets the honest GENUINE <c>no-deliverables-captured</c> verdict (an
/// agent pass CAN fix "produced nothing"), and that the rebuilt world never outlives the grade.
/// </summary>
[Trait("Category", "Unit")]
public class CapturedDeliverableGradeTests
{
    [Fact]
    public async Task The_captured_deliverables_are_rebuilt_under_their_own_paths_and_graded_by_the_per_kind_oracle()
    {
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var (grader, oracle, _) = New(new BenchmarkGrade { Passed = true, Detail = "artifact-present" },
            Row(runId, teamId, "report.md", "# findings\n"),
            Row(runId, teamId, "data/rows.csv", "a,b\n1,2\n"));

        var grade = await grader.GradeCapturedAsync(runId, teamId, Spec("report.md"), 60, CancellationToken.None);

        grade.Passed.ShouldBeTrue("the oracle's verdict is returned verbatim — this lane owns the world, never the verdict");
        grade.Detail.ShouldBe("artifact-present");
        oracle.Calls.ShouldBe(1);
        oracle.SeenFiles["report.md"].ShouldBe("# findings\n", "the deliverable's exact bytes, not a summary of them");
        oracle.SeenFiles["data/rows.csv"].ShouldBe("a,b\n1,2\n", "a nested logical path is rebuilt as a nested path");
        oracle.LastSpec!.Command.ShouldBe(new[] { "report.md" });
        oracle.LastTimeoutSeconds.ShouldBe(60);
    }

    [Fact]
    public async Task A_unit_that_captured_nothing_grades_genuine_no_deliverables_captured_never_no_branch_or_repo()
    {
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var (grader, oracle, _) = New(new BenchmarkGrade { Passed = true, Detail = "must-not-be-consulted" });

        var grade = await grader.GradeCapturedAsync(runId, teamId, Spec("report.md"), 60, CancellationToken.None);

        grade.Passed.ShouldBeFalse("an empty world is not a pass — the oracle never even ran");
        grade.Detail.ShouldBe(ISupervisorAcceptanceGrader.NoDeliverablesCaptured, "the literal consumers key on (Rule 8)");
        grade.Class.ShouldBe(GradeFailureClass.Genuine);
        AgentAcceptanceContract.IsInfraFailure(grade, workPresent: false)
            .ShouldBeFalse("nothing about the CHECK failed — the agent produced nothing, which is exactly what another pass can fix");
        oracle.Calls.ShouldBe(0, "there is no world to hand it");
    }

    [Fact]
    public async Task A_superseded_row_an_escaping_path_and_an_unresolvable_artifact_are_all_left_out()
    {
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        var superseded = Row(runId, teamId, "stale.md", "the earlier capture");
        superseded.Row.SupersededByManifestId = Guid.NewGuid();

        var escaping = Row(runId, teamId, "../escaped.md", "outside the world");
        var dangling = Row(runId, teamId, "gone.md", "unreadable");

        var (grader, oracle, store) = New(new BenchmarkGrade { Passed = true, Detail = "ok" },
            superseded, escaping, dangling, Row(runId, teamId, "report.md", "kept"));

        store.Forget(dangling.Row.ContentArtifactId);

        await grader.GradeCapturedAsync(runId, teamId, Spec("report.md"), 60, CancellationToken.None);

        oracle.SeenFiles.Keys.ShouldBe(new[] { "report.md" });
        oracle.SeenFiles["report.md"].ShouldBe("kept");
    }

    [Fact]
    public async Task The_rebuilt_world_is_torn_down_even_when_the_oracle_throws()
    {
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var (grader, oracle, _) = New(new BenchmarkGrade { Passed = true, Detail = "ok" }, Row(runId, teamId, "report.md", "x"));
        oracle.Throw = new InvalidOperationException("the oracle exploded");

        var grade = await grader.GradeCapturedAsync(runId, teamId, Spec("report.md"), 60, CancellationToken.None);

        grade.Passed.ShouldBeFalse();
        grade.Class.ShouldBe(GradeFailureClass.GraderFault, "the check never RAN — never a verdict on the work");
        Directory.Exists(oracle.LastDirectory!).ShouldBeFalse("the rebuilt world outlives neither the verdict nor the throw");
    }

    // ─── Fixtures ───

    private static SupervisorAcceptanceSpec Spec(params string[] paths) =>
        new() { Command = paths, Kind = BenchmarkGradingKind.ArtifactPresent };

    private static (SupervisorAcceptanceGrader Grader, RecordingOracle Oracle, FakeArtifactStore Store) New(BenchmarkGrade grade, params Deliverable[] deliverables)
    {
        var store = new FakeArtifactStore();

        foreach (var deliverable in deliverables) store.Put(deliverable.Row.ContentArtifactId, deliverable.Bytes);

        var oracle = new RecordingOracle { Grade = grade };
        var manifests = new FakeArtifactManifestStore(deliverables.Select(d => d.Row).ToList());

        var grader = new SupervisorAcceptanceGrader(null!, null!, new StubRunnerRegistry(), new SingleGraderRegistry(oracle), null!, store, manifests, NullLogger<SupervisorAcceptanceGrader>.Instance);

        return (grader, oracle, store);
    }

    /// <summary>One captured deliverable as the store holds it: the manifest row, and the CAS bytes it points at.</summary>
    private sealed record Deliverable(ArtifactManifest Row, byte[] Bytes);

    private static Deliverable Row(Guid runId, Guid teamId, string logicalPath, string content) => new(
        new ArtifactManifest
        {
            Id = Guid.NewGuid(), TeamId = teamId, AgentRunId = runId, FenceEpoch = 1,
            LogicalPath = logicalPath, ContentArtifactId = Guid.NewGuid(),
            Kind = ArtifactManifestStore.KindFor(logicalPath), ContentType = ArtifactManifestStore.ContentTypeFor(logicalPath),
        },
        Encoding.UTF8.GetBytes(content));

    private sealed class FakeArtifactManifestStore : IArtifactManifestStore
    {
        private readonly IReadOnlyList<ArtifactManifest> _rows;

        public FakeArtifactManifestStore(IReadOnlyList<ArtifactManifest> rows) => _rows = rows;

        public Task<IReadOnlyList<ArtifactManifest>> ListForAgentRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ArtifactManifest>>(_rows.Where(r => r.AgentRunId == agentRunId && r.TeamId == teamId).ToList());

        public Task<int> CaptureDeclaredAsync(AgentTask task, string workspaceDirectory, Guid agentRunId, Guid? workflowRunId, Guid teamId, long fenceEpoch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UndeclaredCaptureOutcome> CaptureUndeclaredAsync(AgentTask task, string workspaceDirectory, Guid agentRunId, Guid? workflowRunId, Guid teamId, long fenceEpoch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ArtifactManifest>> ListForWorkflowRunAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeArtifactStore : IArtifactStore
    {
        private readonly Dictionary<Guid, byte[]> _bytes = new();

        public void Put(Guid id, byte[] bytes) => _bytes[id] = bytes;
        public void Forget(Guid id) => _bytes.Remove(id);

        public Task<ArtifactBytes?> GetBytesAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken) =>
            Task.FromResult(_bytes.TryGetValue(artifactId, out var bytes)
                ? new ArtifactBytes { Id = artifactId, Sha256 = "", ContentType = "text/plain", Bytes = bytes }
                : null);

        public Task<Guid> PutAsync(Guid teamId, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken cancellationToken) => Task.FromResult(Guid.NewGuid());
        public Task<ArtifactMetadata?> GetMetadataAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>Reads back the world it was handed — the ONLY way to prove the rebuild is faithful without a real oracle's own semantics in the way.</summary>
    private sealed class RecordingOracle : IBenchmarkGrader
    {
        public BenchmarkGradingKind Kind => BenchmarkGradingKind.ArtifactPresent;
        public BenchmarkGrade Grade { get; set; } = new() { Passed = true, Detail = "ok" };
        public Exception? Throw { get; set; }
        public int Calls { get; private set; }
        public string? LastDirectory { get; private set; }
        public SupervisorAcceptanceSpec? LastSpec { get; private set; }
        public int LastTimeoutSeconds { get; private set; }
        public Dictionary<string, string> SeenFiles { get; } = new(StringComparer.Ordinal);

        public Task<BenchmarkGrade> GradeAsync(BenchmarkGradingContext context, CancellationToken cancellationToken)
        {
            Calls++;
            LastDirectory = context.WorkspaceDirectory;
            LastSpec = context.Acceptance;
            LastTimeoutSeconds = context.Task.TimeoutSeconds;

            foreach (var file in Directory.EnumerateFiles(context.WorkspaceDirectory!, "*", SearchOption.AllDirectories))
                SeenFiles[Path.GetRelativePath(context.WorkspaceDirectory!, file).Replace(Path.DirectorySeparatorChar, '/')] = File.ReadAllText(file);

            if (Throw is { } ex) throw ex;

            return Task.FromResult(Grade);
        }
    }

    private sealed class SingleGraderRegistry : IBenchmarkGraderRegistry
    {
        private readonly IBenchmarkGrader _grader;

        public SingleGraderRegistry(IBenchmarkGrader grader) => _grader = grader;

        public IBenchmarkGrader Resolve(BenchmarkGradingKind kind) => _grader;
    }

    /// <summary>The runner is never actually driven here (the recording oracle runs no command) — it only has to be non-null for the grading context.</summary>
    private sealed class StubRunnerRegistry : ISandboxRunnerRegistry
    {
        public ISandboxRunner Resolve(string kind) => null!;

        public IReadOnlyList<ISandboxRunner> All => Array.Empty<ISandboxRunner>();
    }
}
