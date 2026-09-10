using System.Security.Cryptography;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Exceptions;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
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
    public async Task A_large_declared_file_is_captured_exactly_without_materializing_it_at_the_manifest_seam()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var agentRunId = Guid.NewGuid();
        var length = ArtifactManifestStore.MaxArtifactBytes + 1;

        using var workspace = new TempWorkspace();
        workspace.SetLength("data/large.csv", length);
        using var expectedContent = File.OpenRead(System.IO.Path.Combine(workspace.Path, "data/large.csv"));
        var expectedSha = Convert.ToHexStringLower(SHA256.HashData(expectedContent));

        using var scope = _fixture.BeginScope();
        var store = scope.Resolve<IArtifactManifestStore>();

        var captured = await store.CaptureDeclaredAsync(Task("data/large.csv"), workspace.Path, agentRunId, null, teamId, fenceEpoch: 1, CancellationToken.None);

        captured.ShouldBe(1, "the former byte-array cap is not a durable-deliverable limit; large content must stream to the artifact store");
        var manifest = (await store.ListForAgentRunAsync(agentRunId, teamId, CancellationToken.None)).ShouldHaveSingleItem();
        manifest.SizeBytes.ShouldBe(length);
        var metadata = (await scope.Resolve<IArtifactStore>().GetMetadataAsync(teamId, manifest.ContentArtifactId, CancellationToken.None)).ShouldNotBeNull();
        metadata.SizeBytes.ShouldBe(length);
        manifest.Sha256.ShouldBe(expectedSha, "streaming must preserve the complete file identity, not only report a self-consistent store result");
        metadata.Sha256.ShouldBe(manifest.Sha256, "the manifest must reuse the identity admitted by the streaming store, not re-read a mutable workspace path");
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
    public async Task A_reattached_runs_recapture_at_a_bumped_epoch_supersedes_the_stale_epoch_row()
    {
        // The gap: a reconciler reattach bumps agent_run.fence_epoch (N → N+1) after a mid-run capture at N already
        // landed a manifest row. UpsertAsync superseded same-epoch rows only, so the epoch-N row and the fresh
        // epoch-(N+1) row both read current — a plain ownership-fence reattach, no zombie writer required.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var agentRunId = Guid.NewGuid();
        await SeedRunningAgentRunAsync(teamId, agentRunId, fenceEpoch: 1);

        using var workspace = new TempWorkspace();
        workspace.Write("report.md", "epoch one draft");
        var task = Task("report.md");

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IArtifactManifestStore>().CaptureDeclaredAsync(task, workspace.Path, agentRunId, null, teamId, fenceEpoch: 1, CancellationToken.None)).ShouldBe(1);

        long reattachedEpoch;
        using (var reclaim = _fixture.BeginScope())
            reattachedEpoch = (await reclaim.Resolve<IAgentRunService>().ReserveReattachAsync(agentRunId, CancellationToken.None)).ShouldNotBeNull().Epoch;

        reattachedEpoch.ShouldBe(2, "the premise: the reconciler moved the run to a fresh epoch after the epoch-1 capture already landed");

        workspace.Write("report.md", "epoch two — the reattached attempt's final draft");

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IArtifactManifestStore>().CaptureDeclaredAsync(task, workspace.Path, agentRunId, null, teamId, reattachedEpoch, CancellationToken.None)).ShouldBe(1);

        using var reader = _fixture.BeginScope();
        var rows = await reader.Resolve<CodeSpaceDbContext>().ArtifactManifest.AsNoTracking()
            .Where(m => m.AgentRunId == agentRunId).OrderBy(m => m.FenceEpoch).ToListAsync();

        rows.Count.ShouldBe(2);
        rows[0].FenceEpoch.ShouldBe(1);
        rows[1].FenceEpoch.ShouldBe(2);
        rows[1].SupersededByManifestId.ShouldBeNull("the reattached epoch's row is current");
        rows[0].SupersededByManifestId.ShouldBe(rows[1].Id, "the stale epoch-1 row must be superseded by the epoch-2 recapture — not left dangling as a second current copy");

        (await reader.Resolve<IArtifactManifestStore>().ListForAgentRunAsync(agentRunId, teamId, CancellationToken.None))
            .Count(m => m.SupersededByManifestId == null).ShouldBe(1, "exactly one current deliverable for this path, regardless of which epoch produced it");
    }

    [Fact]
    public async Task A_stale_epoch_write_after_a_later_epoch_already_recorded_is_refused()
    {
        // Belt-and-braces: production can't reach this state (AgentRunService.AssertOwnershipAsync refuses a
        // reclaimed worker before it ever calls back into this store), but the store must not compound the mistake
        // if some caller ever bypasses that fence — a write at an epoch the identity has already moved past must
        // THROW (an observable refusal, never a reported success), and it must never insert or supersede the later
        // epoch's current row.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var agentRunId = Guid.NewGuid();
        await SeedRunningAgentRunAsync(teamId, agentRunId, fenceEpoch: 1);

        using var workspace = new TempWorkspace();
        workspace.Write("report.md", "epoch one draft");
        var task = Task("report.md");

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IArtifactManifestStore>().CaptureDeclaredAsync(task, workspace.Path, agentRunId, null, teamId, fenceEpoch: 1, CancellationToken.None);

        using (var reclaim = _fixture.BeginScope())
            (await reclaim.Resolve<IAgentRunService>().ReserveReattachAsync(agentRunId, CancellationToken.None)).ShouldNotBeNull().Epoch.ShouldBe(2);

        workspace.Write("report.md", "epoch two — the reattached attempt's final draft");

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IArtifactManifestStore>().CaptureDeclaredAsync(task, workspace.Path, agentRunId, null, teamId, fenceEpoch: 2, CancellationToken.None);

        workspace.Write("report.md", "a stale epoch-1 write arriving late");

        // The bytes still resolve and stream to the CAS store (the retention declaration is unconditional, see
        // ArtifactManifestStore.CaptureOneAsync) — the manifest pointer itself is the thing under test here. The
        // caller must see this refusal, not a captured count that reads like the write landed.
        using (var stale = _fixture.BeginScope())
            await Should.ThrowAsync<AgentRunOwnershipLostException>(() => stale.Resolve<IArtifactManifestStore>().CaptureDeclaredAsync(task, workspace.Path, agentRunId, null, teamId, fenceEpoch: 1, CancellationToken.None));

        using var reader = _fixture.BeginScope();
        var rows = await reader.Resolve<CodeSpaceDbContext>().ArtifactManifest.AsNoTracking()
            .Where(m => m.AgentRunId == agentRunId).OrderBy(m => m.FenceEpoch).ToListAsync();

        rows.Count.ShouldBe(2, "the refused stale write must not append a third row");
        rows[1].FenceEpoch.ShouldBe(2);
        rows[1].SupersededByManifestId.ShouldBeNull("the stale epoch-1 write must never supersede the epoch-2 row");
        rows[0].SupersededByManifestId.ShouldBe(rows[1].Id, "the epoch-1 row's existing supersession is untouched by the refused replay");
    }

    private async Task SeedRunningAgentRunAsync(Guid teamId, Guid runId, long fenceEpoch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.AgentRun.Add(new AgentRun
        {
            Id = runId, TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Running, FenceEpoch = fenceEpoch,
            LeaseExpiresAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(1), CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
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

        public void SetLength(string relative, long length)
        {
            var full = System.IO.Path.Combine(Path, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            using var file = File.Create(full);
            file.SetLength(length);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
