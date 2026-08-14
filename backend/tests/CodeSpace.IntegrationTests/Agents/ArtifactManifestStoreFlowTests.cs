using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟢 Integration (real Postgres + real filesystem + real CAS store): DC-4's declared-artifact capture end to
/// end — a non-TestsPass acceptance's declared paths become TYPED artifact-manifest rows whose bytes live in the
/// CAS store; a re-capture of identical bytes is the exactly-once no-op; changed bytes supersede with a pointer
/// (never a rewrite — the #1352 discipline); a TestsPass task captures nothing.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ArtifactManifestStoreFlowTests
{
    private readonly PostgresFixture _fixture;

    public ArtifactManifestStoreFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Declared_paths_become_typed_rows_with_bytes_in_the_store()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var agentRunId = Guid.NewGuid();
        var workflowRunId = Guid.NewGuid();

        using var workspace = new TempWorkspace();
        workspace.Write("docs/report.md", "# findings\n");
        workspace.Write("data/rows.csv", "a,b\n1,2\n");

        using var scope = _fixture.BeginScope();
        var store = scope.Resolve<IArtifactManifestStore>();

        var captured = await store.CaptureDeclaredAsync(Task("docs/report.md", "data/rows.csv", "missing/none.md"), workspace.Path, agentRunId, workflowRunId, teamId, fenceEpoch: 1, CancellationToken.None);

        captured.ShouldBe(2, "the two real files capture; the missing declared path is skipped (the acceptance oracle is the one that fails over it)");

        var rows = await store.ListForAgentRunAsync(agentRunId, teamId, CancellationToken.None);
        rows.Count.ShouldBe(2);

        var report = rows.Single(r => r.LogicalPath == "docs/report.md");
        report.Kind.ShouldBe(ArtifactManifestKind.Document);
        report.ContentType.ShouldBe("text/markdown");
        report.WorkflowRunId.ShouldBe(workflowRunId);

        var stored = await scope.Resolve<IArtifactStore>().GetBytesAsync(teamId, report.ContentArtifactId, CancellationToken.None);
        System.Text.Encoding.UTF8.GetString(stored!.Bytes).ShouldBe("# findings\n", "the CAS row holds the exact captured bytes");

        rows.Single(r => r.LogicalPath == "data/rows.csv").Kind.ShouldBe(ArtifactManifestKind.Dataset);
    }

    [Fact]
    public async Task A_recapture_is_exactly_once_and_a_changed_file_supersedes_with_a_pointer()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var agentRunId = Guid.NewGuid();

        using var workspace = new TempWorkspace();
        workspace.Write("report.md", "v1");

        using var scope = _fixture.BeginScope();
        var store = scope.Resolve<IArtifactManifestStore>();
        var task = Task("report.md");

        await store.CaptureDeclaredAsync(task, workspace.Path, agentRunId, null, teamId, 1, CancellationToken.None);
        await store.CaptureDeclaredAsync(task, workspace.Path, agentRunId, null, teamId, 1, CancellationToken.None);

        (await store.ListForAgentRunAsync(agentRunId, teamId, CancellationToken.None))
            .ShouldHaveSingleItem("identical bytes at the same coordinates are the exactly-once no-op");

        workspace.Write("report.md", "v2 — revised");
        await store.CaptureDeclaredAsync(task, workspace.Path, agentRunId, null, teamId, 1, CancellationToken.None);

        var rows = await scope.Resolve<CodeSpaceDbContext>().ArtifactManifest.AsNoTracking()
            .Where(m => m.AgentRunId == agentRunId).OrderBy(m => m.CreatedDate).ToListAsync();

        rows.Count.ShouldBe(2, "a changed capture appends — never rewrites");
        rows[0].SupersededByManifestId.ShouldBe(rows[1].Id, "the prior row points at its successor (the #1352 discipline)");
        rows[1].SupersededByManifestId.ShouldBeNull("the fresh row is current");
    }

    [Fact]
    public async Task A_tests_pass_task_captures_nothing()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var workspace = new TempWorkspace();
        workspace.Write("dotnet", "not-an-artifact");

        using var scope = _fixture.BeginScope();
        var captured = await scope.Resolve<IArtifactManifestStore>().CaptureDeclaredAsync(
            new AgentTask { Goal = "g", Harness = "codex-cli", Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "dotnet", "test" } } },
            workspace.Path, Guid.NewGuid(), null, teamId, 1, CancellationToken.None);

        captured.ShouldBe(0, "a TestsPass Command is an argv, never a deliverable list");
    }

    private static AgentTask Task(params string[] paths) => new()
    {
        Goal = "produce the declared deliverables", Harness = "codex-cli",
        Acceptance = new SupervisorAcceptanceSpec { Command = paths, Kind = BenchmarkGradingKind.ArtifactPresent },
    };

    private sealed class TempWorkspace : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs-artifact-flow-" + Guid.NewGuid().ToString("N"));

        public TempWorkspace() => Directory.CreateDirectory(Path);

        public void Write(string relative, string content)
        {
            var full = System.IO.Path.Combine(Path, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
