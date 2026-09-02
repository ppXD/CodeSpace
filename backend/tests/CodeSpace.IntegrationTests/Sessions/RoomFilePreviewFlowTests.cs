using CodeSpace.Core.Settings;
using System.IO;
using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Backends;
using CodeSpace.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
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
    public async Task A_large_cjk_file_projects_a_real_utf8_byte_bounded_room_payload()
    {
        const int maxPreviewBytes = 512 * 1024;
        var body = string.Concat(Enumerable.Repeat("界", maxPreviewBytes / 3 + 2));
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTurnWithAgentAsync(teamId, changedFiles: new[] { "cjk.md" }, patch: AddedFile("cjk.md", body));

        var preview = await PreviewAsync(runId, "cjk.md", teamId);

        preview.ShouldNotBeNull();
        preview!.Kind.ShouldBe("text");
        preview.Text.ShouldNotBeNull();
        preview.Note.ShouldNotBeNull();
        preview.SizeBytes.ShouldBe(Encoding.UTF8.GetByteCount(body), "SizeBytes describes the complete reconstructed file, not the excerpt");
        Encoding.UTF8.GetByteCount(preview.Text!).ShouldBeLessThanOrEqualTo(maxPreviewBytes, "the DTO handed to the API serializer is itself byte-bounded; the frontend never receives a 1.5 MiB CJK '512K-char' body");
        preview.Text!.ShouldNotContain('\uFFFD');
        preview.Truncated.ShouldBeTrue();
        preview.Note!.ShouldContain("truncated");
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
    public async Task A_path_only_multi_repo_preview_fails_closed_when_two_repositories_changed_the_same_path()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);
        var agentRunId = await AddMultiRepoAgentAsync(teamId, runId,
            (Guid.NewGuid(), "web", "README.md", "WEB"),
            (Guid.NewGuid(), "api", "README.md", "API"));

        var preview = await PreviewAsync(runId, "README.md", teamId, agentRunId);

        preview.ShouldNotBeNull();
        preview!.Kind.ShouldBe("unavailable", "path + agent is not a repository identity when two repos carry that path; selecting the first per-repo result would show the wrong file");
        preview.Text.ShouldBeNull();
        preview.Note.ShouldContain("repository", customMessage: "the display must say why the path cannot be determined, never present an arbitrary sibling repo's bytes");
    }

    [Fact]
    public async Task Exact_multi_repo_identity_previews_only_the_requested_repository()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);
        var webId = Guid.NewGuid();
        var apiId = Guid.NewGuid();
        var agentRunId = await AddMultiRepoAgentAsync(teamId, runId,
            (webId, "web", "README.md", "WEB"),
            (apiId, "api", "README.md", "API"));

        var preview = await PreviewIdentityAsync(runId, teamId, new RoomFileIdentity { Path = "README.md", AgentRunId = agentRunId, RepositoryId = apiId, RepositoryAlias = "api" });

        preview.ShouldNotBeNull();
        preview!.Text.ShouldBe("API", "the exact api identity must not return the first/web per-repo result carrying the same path");
        preview.Identity.ShouldBe(new RoomFileIdentity { Path = "README.md", AgentRunId = agentRunId, RepositoryId = apiId, RepositoryAlias = "api" });
    }

    [Fact]
    public async Task A_mismatched_repository_id_and_alias_never_fall_back_to_a_sibling()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);
        var webId = Guid.NewGuid();
        var apiId = Guid.NewGuid();
        var agentRunId = await AddMultiRepoAgentAsync(teamId, runId,
            (webId, "web", "README.md", "WEB"),
            (apiId, "api", "README.md", "API"));

        var preview = await PreviewIdentityAsync(runId, teamId, new RoomFileIdentity { Path = "README.md", AgentRunId = agentRunId, RepositoryId = webId, RepositoryAlias = "api" });

        preview.ShouldNotBeNull();
        preview!.Kind.ShouldBe("unavailable");
        preview.UnavailableReason.ShouldBe(RoomFileUnavailableReason.NotInChangeSet);
        preview.Text.ShouldBeNull();
    }

    [Fact]
    public async Task An_exact_manifest_can_supply_the_offloaded_patch_without_artifact_n_plus_one()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);
        var webId = Guid.NewGuid();
        var apiId = Guid.NewGuid();
        var agentRunId = await AddMultiRepoAgentAsync(teamId, runId,
            (webId, "web", "README.md", "WEB"),
            (apiId, "api", "README.md", ""));
        await AddManifestAsync(teamId, runId, agentRunId, apiId, "api", "README.md", AddedFile("README.md", "API FROM MANIFEST"));

        var preview = await PreviewIdentityAsync(runId, teamId, new RoomFileIdentity { Path = "README.md", AgentRunId = agentRunId, RepositoryId = apiId, RepositoryAlias = "api" });

        preview.ShouldNotBeNull();
        preview!.Text.ShouldBe("API FROM MANIFEST", "the exact per-repo manifest is the durable patch carrier when the result's inline carrier is empty");
    }

    [Fact]
    public async Task A_missing_offloaded_patch_reports_the_typed_storage_fact_instead_of_fake_expiry_or_a_500()
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
        preview.UnavailableReason.ShouldBe(RoomFileUnavailableReason.PhysicalObjectMissing);
        preview.Note.ShouldNotBeNull();
        preview.Note!.ShouldContain("stored bytes are missing");
        preview.Note.ShouldNotContain("expired", customMessage: "there is no expiry policy and a topology/integrity fault must not be relabelled as normal expiry");
        preview.Note.ShouldNotContain("pull request", customMessage: "this run has no delivered source URL, so the UI must not recommend an action that does not exist");
    }


    [Fact]
    public async Task Files_changed_opens_a_patch_whose_bytes_live_at_a_storage_provider()
    {
        // The surface an operator actually clicks, over the one shape a configured deployment always has. Every other
        // test in this file seeds an inline patch or a local storage_url, so before this the routed read behind the
        // diff drawer had never been proven to render at all.
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var destination = await RoutedArtifactSeed.RouteTeamAsync(_fixture, teamId, actorId);
        var patchArtifactId = await RoutedArtifactSeed.WriteRoutedAsync(_fixture, teamId, AddedFile("docs/routed.md", "from the provider"));

        destination.ObjectCount.ShouldBe(1, "the patch must physically be at the provider, or this proves nothing about a routed read");

        var runId = await SeedRunAsync(teamId);
        await AddOffloadedAgentAsync(teamId, runId, new[] { "docs/routed.md" }, patchArtifactId);

        var preview = await PreviewAsync(runId, "docs/routed.md", teamId);

        preview.ShouldNotBeNull("a routed patch must render exactly like an inline one");
        preview!.Kind.ShouldBe("text");
        preview.ChangeKind.ShouldBe("Added");
        preview.Text.ShouldBe("from the provider");
        preview.UnavailableReason.ShouldBeNull();
    }

    [Fact]
    public async Task Files_changed_still_opens_a_routed_patch_after_the_team_repoints_its_route()
    {
        // The guarantee the whole location ledger exists for, checked at the surface a user touches rather than at the
        // store. A read that consulted today's route would look for these bytes in the NEW destination and find
        // nothing there.
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var first = await RoutedArtifactSeed.RouteTeamAsync(_fixture, teamId, actorId);
        var patchArtifactId = await RoutedArtifactSeed.WriteRoutedAsync(_fixture, teamId, AddedFile("docs/routed.md", "written before the switch"));

        var runId = await SeedRunAsync(teamId);
        await AddOffloadedAgentAsync(teamId, runId, new[] { "docs/routed.md" }, patchArtifactId);

        await RepointRouteAsync(teamId, actorId, first.RouteId);

        var preview = await PreviewAsync(runId, "docs/routed.md", teamId);

        preview.ShouldNotBeNull("the patch resolves through the profile revision its own location recorded, never through today's route");
        preview!.Text.ShouldBe("written before the switch");
        first.ObjectCount.ShouldBe(1, "the bytes never moved — only the route did");
    }

    [Fact]
    public async Task A_lost_patch_names_why_its_bytes_were_never_stored()
    {
        // Deliverable-loss honesty: the offload was refused (or the oversize inline copy was shed), so NO carrier
        // exists — but the result row NAMES the loss. The preview must render that name, never a bare
        // metadata-missing shrug the operator has to chase with SQL.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId);
        await AddLossyAgentAsync(teamId, runId, new[] { "docs/analysis/pr-inventory.md" }, "the patch's bytes were shed after the store refused the offload — HttpRequestException: 403 from the destination");

        var preview = await PreviewAsync(runId, "docs/analysis/pr-inventory.md", teamId);

        preview.ShouldNotBeNull();
        preview!.Kind.ShouldBe("unavailable");
        preview.UnavailableReason.ShouldBe(RoomFileUnavailableReason.MetadataMissing);
        preview.Note.ShouldNotBeNull();
        preview.Note!.ShouldContain("never stored", customMessage: "the loss must be named AS a loss — not rendered as generic missing metadata");
        preview.Note.ShouldContain("403", customMessage: "…and carry the recorded reason verbatim, so nobody digs with SQL");
    }

    [Fact]
    public async Task A_routed_patch_whose_object_is_gone_is_an_unavailable_preview_that_names_the_reason()
    {
        // The failure a user hits when a destination stops serving. It must arrive as a typed reason the drawer can
        // render, not as a 500 and not as a blank file.
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var destination = await RoutedArtifactSeed.RouteTeamAsync(_fixture, teamId, actorId);
        var patchArtifactId = await RoutedArtifactSeed.WriteRoutedAsync(_fixture, teamId, AddedFile("docs/routed.md", "about to vanish"));

        Directory.Delete(destination.Root, recursive: true);

        var runId = await SeedRunAsync(teamId);
        await AddOffloadedAgentAsync(teamId, runId, new[] { "docs/routed.md" }, patchArtifactId);

        var preview = await PreviewAsync(runId, "docs/routed.md", teamId);

        preview.ShouldNotBeNull("bytes that cannot be fetched must still produce a preview that says so");
        preview!.UnavailableReason.ShouldNotBeNull("without a typed reason the drawer can only apologise, and an operator cannot tell a purged artifact from a broken credential");
    }

    /// <summary>Points the team's workflow-artifact route at a second, empty destination — the shape of an operator switching provider.</summary>
    private async Task RepointRouteAsync(Guid teamId, Guid actorId, Guid routeId)
    {
        var next = await RoutedArtifactSeed.RouteTeamAsync(_fixture, teamId, actorId, dataClassTypeKey: "unused/v1");

        // APPENDED, never edited: storage_route_revision is immutable by trigger, which is the same property that
        // makes a stamped location trustworthy. A repoint is a new revision the route's head moves to.
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        // The append and the head advance must land in ONE statement batch: 0134 refuses a revision whose route head
        // did not move with it, so a two-step edit is rejected rather than leaving a route pointing at a revision it
        // never adopted.
        var route = await db.StorageRoute.SingleAsync(value => value.Id == routeId);
        route.CurrentRevision = 2;
        route.LastModifiedDate = DateTimeOffset.UtcNow;
        db.StorageRouteRevision.Add(new StorageRouteRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageRouteId = routeId, Revision = 2, StorageProfileId = next.ProfileId,
            ProfileRevisionMode = StorageProfileRevisionMode.CurrentAtWrite, PinnedProfileRevision = null,
            CreatedDate = DateTimeOffset.UtcNow, CreatedBy = actorId,
        });
        await db.SaveChangesAsync();
    }

    private async Task<RoomFilePreview?> PreviewAsync(Guid runId, string path, Guid teamId, Guid? agentRunId = null)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IRoomFilePreviewService>().PreviewAsync(runId, path, teamId, agentRunId, CancellationToken.None);
    }

    private async Task<RoomFilePreview?> PreviewIdentityAsync(Guid runId, Guid teamId, RoomFileIdentity identity)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IRoomFilePreviewService>().PreviewAsync(runId, identity, teamId, CancellationToken.None);
    }

    /// <summary>Seed a workflow_artifact METADATA row whose blob is MISSING — an offloaded storage_url UNDER the store root (so it passes the backend's under-root check) pointing at a sharded file that was never written. Reading it throws FileNotFoundException, exactly like a temp-cleaned artifact.</summary>
    private async Task<Guid> SeedPurgedPatchArtifactAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var sha = Convert.ToHexString(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray()).ToLowerInvariant();   // 64 hex, unique → never on disk
        var root = DurableRoots.ArtifactStore(RuntimeSettings.Current.ArtifactStoreDirectory);
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

    private async Task AddLossyAgentAsync(Guid teamId, Guid runId, IReadOnlyList<string> changedFiles, string lossReason)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", ChangedFiles = changedFiles.ToList(), Patch = "", PatchLossReason = lossReason };
        db.AgentRun.Add(new AgentRun { Id = Guid.NewGuid(), TeamId = teamId, WorkflowRunId = runId, Harness = "codex-cli", Status = AgentRunStatus.Succeeded, CreatedDate = DateTimeOffset.UtcNow, ResultJson = JsonSerializer.Serialize(result, AgentJson.Options) });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> AddMultiRepoAgentAsync(Guid teamId, Guid runId, params (Guid RepositoryId, string Alias, string Path, string Body)[] repositories)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var agentRunId = Guid.NewGuid();
        var primary = repositories[0];
        var result = new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded,
            ExitReason = "completed",
            ChangedFiles = new[] { primary.Path },
            Patch = AddedFile(primary.Path, primary.Body),
            RepositoryResults = repositories.Select(repository => new RepositoryRunResult
            {
                RepositoryId = repository.RepositoryId,
                Alias = repository.Alias,
                ChangedFiles = new[] { repository.Path },
                Patch = AddedFile(repository.Path, repository.Body),
            }).ToList(),
        };

        db.AgentRun.Add(new AgentRun
        {
            Id = agentRunId, TeamId = teamId, WorkflowRunId = runId, Harness = "codex-cli",
            Status = AgentRunStatus.Succeeded, CreatedDate = DateTimeOffset.UtcNow, ResultJson = JsonSerializer.Serialize(result, AgentJson.Options),
        });

        await db.SaveChangesAsync();
        return agentRunId;
    }

    private async Task AddManifestAsync(Guid teamId, Guid runId, Guid agentRunId, Guid repositoryId, string alias, string path, string patch)
    {
        Guid artifactId;
        using (var artifactScope = _fixture.BeginScope())
            artifactId = await artifactScope.Resolve<IArtifactStore>().PutAsync(teamId, System.Text.Encoding.UTF8.GetBytes(patch), "text/x-diff", CancellationToken.None);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.PublishManifest.Add(new PublishManifest
        {
            Id = Guid.NewGuid(), TeamId = teamId, WorkflowRunId = runId, AgentRunId = agentRunId, Kind = PublishManifestKind.Agent,
            RepositoryId = repositoryId, RepositoryAlias = alias, PatchArtifactId = artifactId, ChangedFileCount = 1,
            ChangedFilesJson = JsonSerializer.Serialize(new[] { path }), PublishStateValue = PublishState.PatchOnly,
            CreatedDate = now, CreatedBy = SystemUsers.SeederId, LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
    }
}
