using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Sessions.Room;
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

    private async Task<RoomFilePreview?> PreviewAsync(Guid runId, string path, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IRoomFilePreviewService>().PreviewAsync(runId, path, teamId, CancellationToken.None);
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

    private async Task AddAgentAsync(Guid teamId, Guid runId, AgentRunStatus status, IReadOnlyList<string> changedFiles, string patch, DateTimeOffset createdDate)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var result = new AgentRunResult
        {
            Status = status, ExitReason = status == AgentRunStatus.Succeeded ? "completed" : "non-zero-exit",
            ChangedFiles = changedFiles.ToList(), Patch = patch,
        };
        db.AgentRun.Add(new AgentRun
        {
            Id = Guid.NewGuid(), TeamId = teamId, WorkflowRunId = runId, Harness = "codex-cli",
            Status = status, CreatedDate = createdDate, ResultJson = JsonSerializer.Serialize(result, AgentJson.Options),
        });

        await db.SaveChangesAsync();
    }
}
