using System.IO;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Core.Services.Workflows.Artifacts.Backends;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Sessions;

/// <summary>
/// The generic file-content preview over real Postgres — the DB assembly the pure <see cref="UnifiedPatchReader"/>
/// can't cover: locating the producing agent by the turn's run id, resolving its captured (inline) patch through the
/// real artifact offloader, and tenancy (a foreign run is an indistinguishable null). The diff-parse richness is proven
/// exhaustively at the unit tier; this proves the wiring + the persistence reads.
///
/// <para>Tier: high-fidelity Integration — the real <see cref="IRoomFilePreviewService"/> + its dependencies over real Postgres.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RoomFilePreviewFlowTests
{
    private const string AddedMd =
        "diff --git a/docs/plan.md b/docs/plan.md\n" +
        "new file mode 100644\n" +
        "index 0000000..1111111\n" +
        "--- /dev/null\n" +
        "+++ b/docs/plan.md\n" +
        "@@ -0,0 +1,2 @@\n" +
        "+# Plan\n" +
        "+Ship it.\n";

    private readonly PostgresFixture _fixture;

    public RoomFilePreviewFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task An_added_file_previews_its_full_reconstructed_content()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTurnWithAgentAsync(teamId, changedFiles: new[] { "docs/plan.md" }, patch: AddedMd);

        var preview = await PreviewAsync(runId, "docs/plan.md", teamId);

        preview.ShouldNotBeNull();
        preview!.Kind.ShouldBe("text");
        preview.ChangeKind.ShouldBe("Added");
        preview.Text.ShouldBe("# Plan\nShip it.");
        preview.Truncated.ShouldBeFalse();
    }

    [Fact]
    public async Task A_path_outside_the_change_set_is_a_graceful_unavailable_preview()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTurnWithAgentAsync(teamId, changedFiles: new[] { "docs/plan.md" }, patch: AddedMd);

        var preview = await PreviewAsync(runId, "src/not-touched.cs", teamId);

        preview.ShouldNotBeNull();
        preview!.Kind.ShouldBe("unavailable");
        preview.Text.ShouldBeNull();
    }

    [Fact]
    public async Task A_misattributed_result_file_falls_back_to_the_turn_scan_instead_of_failing()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);
        var now = DateTimeOffset.UtcNow;

        // The REAL producer of out.md.
        await AddAgentAsync(teamId, runId, AgentRunStatus.Succeeded, new[] { "out.md" }, AddedFile("out.md", "hello"), now.AddMinutes(-1));
        // A DIFFERENT accepted agent the RESULT card mis-attributed the file to (last-writer-wins) — its OWN change set lacks out.md.
        var misattributed = await AddAgentAsync(teamId, runId, AgentRunStatus.Succeeded, new[] { "other.md" }, AddedFile("other.md", "x"), now);

        var preview = await PreviewAsync(runId, "out.md", teamId, agentRunId: misattributed);

        preview.ShouldNotBeNull();
        preview!.Kind.ShouldBe("text", "the scoped agent lacked the file, but the turn-wide fallback found its real producer — NOT an 'isn't part of the change set' error");
        preview.Text.ShouldBe("hello", "reconstructed from the real producer's patch");
    }

    [Fact]
    public async Task A_foreign_team_is_an_indistinguishable_not_found()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (otherTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);
        await AddAgentAsync(teamId, runId, AgentRunStatus.Succeeded, new[] { "docs/plan.md" }, AddedMd, DateTimeOffset.UtcNow);

        (await PreviewAsync(runId, "docs/plan.md", otherTeamId)).ShouldBeNull();
    }

    [Fact]
    public async Task A_failed_agents_rejected_diff_never_overrides_the_accepted_one_for_the_same_path()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);
        var now = DateTimeOffset.UtcNow;

        var accepted = AddedFile("shared.md", "ACCEPTED");
        var rejected = AddedFile("shared.md", "REJECTED");

        await AddAgentAsync(teamId, runId, AgentRunStatus.Succeeded, new[] { "shared.md" }, accepted, now.AddMinutes(-2));
        await AddAgentAsync(teamId, runId, AgentRunStatus.Failed, new[] { "shared.md" }, rejected, now);   // later, but rejected

        var preview = await PreviewAsync(runId, "shared.md", teamId);

        preview.ShouldNotBeNull();
        preview!.Kind.ShouldBe("text");
        preview.Text.ShouldBe("ACCEPTED", "a failed agent's rejected diff must never win last-writer-wins");
    }

    [Fact]
    public async Task Scoping_to_an_agent_returns_that_agents_own_version_of_a_shared_path()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);
        var now = DateTimeOffset.UtcNow;

        // Two agents both add shared.md with DIFFERENT content — the turn-wide preview picks the newest, but scoping to
        // an agent returns THAT agent's own version (per-agent attribution).
        var older = await AddAgentAsync(teamId, runId, AgentRunStatus.Succeeded, new[] { "shared.md" }, AddedFile("shared.md", "OLDER"), now.AddMinutes(-3));
        await AddAgentAsync(teamId, runId, AgentRunStatus.Succeeded, new[] { "shared.md" }, AddedFile("shared.md", "NEWER"), now);

        (await PreviewAsync(runId, "shared.md", teamId))!.Text.ShouldBe("NEWER", "turn-wide resolves to the newest accepted writer");
        (await PreviewAsync(runId, "shared.md", teamId, older))!.Text.ShouldBe("OLDER", "scoping to an agent returns ITS own version");
    }

    [Fact]
    public async Task A_purged_offloaded_patch_degrades_to_an_expired_notice_instead_of_a_500()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);

        // The agent's diff was OFFLOADED (Patch empty, PatchArtifactId set) and its blob has since been PURGED — the
        // durable metadata row lives on, the file is gone (the classic dev case: the store is a temp dir the OS cleaned).
        var artifactId = await SeedPurgedPatchArtifactAsync(teamId);
        await AddOffloadedAgentAsync(teamId, runId, new[] { "docs/plan.md" }, artifactId);

        var preview = await PreviewAsync(runId, "docs/plan.md", teamId);

        preview.ShouldNotBeNull("a purged artifact must degrade gracefully, never propagate as a 500");
        preview!.Kind.ShouldBe("unavailable");
        preview.Text.ShouldBeNull();
        preview.Note.ShouldNotBeNull();
        preview.Note!.ShouldContain("expired", customMessage: "the notice tells the operator the saved content is gone — not a bare 'couldn't load'");
    }

    private async Task<RoomFilePreview?> PreviewAsync(Guid runId, string path, Guid teamId, Guid? agentRunId = null)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IRoomFilePreviewService>().PreviewAsync(runId, path, teamId, agentRunId, CancellationToken.None);
    }

    /// <summary>Seed a workflow_artifact METADATA row whose blob is MISSING — an offloaded storage_url UNDER the store root (so it passes the backend's under-root check) pointing at a sharded file that was never written. Reading it throws FileNotFoundException, exactly like a temp-cleaned artifact.</summary>
    private async Task<Guid> SeedPurgedPatchArtifactAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var sha = Convert.ToHexString(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray()).ToLowerInvariant();   // 64 hex, unique → never on disk
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable(LocalFileArtifactBlobBackend.StoreDirEnvVar) is { Length: > 0 } d ? d : Path.Combine(Path.GetTempPath(), "codespace-artifact-store"));
        var missing = Path.Combine(root, sha[..2], sha.Substring(2, 2), sha);

        var id = Guid.NewGuid();
        db.WorkflowArtifact.Add(new WorkflowArtifact
        {
            Id = id, TeamId = teamId, Sha256 = sha, ContentType = "text/x-diff", SizeBytes = 22193,
            InlineBytes = null, StorageUrl = new Uri(missing).AbsoluteUri,
        });

        await db.SaveChangesAsync();
        return id;
    }

    private async Task AddOffloadedAgentAsync(Guid teamId, Guid runId, IReadOnlyList<string> changedFiles, Guid patchArtifactId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var result = new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded, ExitReason = "completed",
            ChangedFiles = changedFiles.ToList(), Patch = "", PatchArtifactId = patchArtifactId,
        };
        db.AgentRun.Add(new AgentRun
        {
            Id = Guid.NewGuid(), TeamId = teamId, WorkflowRunId = runId, Harness = "codex-cli",
            Status = AgentRunStatus.Succeeded, CreatedDate = DateTimeOffset.UtcNow, ResultJson = JsonSerializer.Serialize(result, AgentJson.Options),
        });

        await db.SaveChangesAsync();
    }

    private static string AddedFile(string path, string body) =>
        $"diff --git a/{path} b/{path}\nnew file mode 100644\nindex 0000000..1111111\n--- /dev/null\n+++ b/{path}\n@@ -0,0 +1,1 @@\n+{body}\n";

    private async Task<Guid> SeedRunAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = JsonSerializer.Serialize(new { goal = "Write the plan" }),
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Snapshot,
            Status = WorkflowRunStatus.Success, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
        return runId;
    }

    private async Task<Guid> SeedTurnWithAgentAsync(Guid teamId, IReadOnlyList<string> changedFiles, string patch)
    {
        var runId = await SeedRunAsync(teamId);
        await AddAgentAsync(teamId, runId, AgentRunStatus.Succeeded, changedFiles, patch, DateTimeOffset.UtcNow);
        return runId;
    }

    private async Task<Guid> AddAgentAsync(Guid teamId, Guid runId, AgentRunStatus status, IReadOnlyList<string> changedFiles, string patch, DateTimeOffset createdDate)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var agentRunId = Guid.NewGuid();
        var result = new AgentRunResult
        {
            Status = status, ExitReason = status == AgentRunStatus.Succeeded ? "completed" : "non-zero-exit",
            ChangedFiles = changedFiles.ToList(), Patch = patch,
        };
        db.AgentRun.Add(new AgentRun
        {
            Id = agentRunId, TeamId = teamId, WorkflowRunId = runId, Harness = "codex-cli",
            Status = status, CreatedDate = createdDate, ResultJson = JsonSerializer.Serialize(result, AgentJson.Options),
        });

        await db.SaveChangesAsync();
        return agentRunId;
    }
}
