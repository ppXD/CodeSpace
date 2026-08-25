using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Retention;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Artifacts;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Rule 12 tier: HIGH fidelity. Real <c>WorkflowSensitivePayloadStore</c>, real artifact plane, real local blob
/// backend, real Postgres — and the 0168 BEFORE INSERT trigger is the mechanism that fails the transaction, not a
/// substituted throw.
///
/// <para>The invariant under test: <b>after a rollback at the sidecar write, no durable ciphertext exists that
/// nothing can ever collect</b>. Encrypted bytes past the inline threshold reach the storage provider before the
/// sidecar row is attempted, and no provider participates in a Postgres rollback — so the only way the bytes stay
/// reclaimable is for their retention DECLARATION to be durable independently of the caller's transaction.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SensitivePayloadRollbackFlowTests
{
    private readonly PostgresFixture _fixture;

    public SensitivePayloadRollbackFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_rollback_at_the_sidecar_write_leaves_the_ciphertext_declared_so_the_reaper_can_reclaim_it()
    {
        var world = await SeedWorldAsync();
        var root = await ArtifactRootAsync(world);

        // The 0168 trigger's own precondition is the lever: a record that is not a same-team node.completed makes the
        // sidecar INSERT raise, which is exactly the shape of every other failure after the bytes have landed.
        var recordId = await SeedRecordAsync(world, "node.started");
        var before = BlobFiles(root);

        await RollbackAtSidecarWriteAsync(world, recordId);

        var landed = BlobFiles(root).Except(before).ToArray();
        landed.Length.ShouldBe(1, "the encrypted bytes reach the provider before the sidecar row is attempted — if they did not, this test proves nothing");

        var declaration = await DeclarationForHolderAsync(recordId);
        declaration.ShouldNotBeNull("bytes the rollback could not remove must still carry a positive retention declaration, or they are unreapable forever");
        declaration.State.ShouldBe(ArtifactRetentionState.Declared);
        declaration.RetentionClass.ShouldBe(nameof(ArtifactRetentionClass.SensitiveRecordPayload));

        var artifact = await ArtifactAsync(declaration.ArtifactId);
        artifact.ShouldNotBeNull("the declaration's artifact row must survive with it — the reaper reaches the bytes through that row's placement");
        new Uri(artifact.StorageUrl.ShouldNotBeNull()).LocalPath.ShouldBe(landed[0], "the surviving declaration must name the very file the rollback left behind");
    }

    [Fact]
    public async Task The_ciphertext_a_rollback_left_behind_is_actually_collected_by_a_real_sweep()
    {
        // The declaration is only worth having if the reaper finishes the job. Nothing references these bytes, so both
        // retention waits elapse and the file goes.
        var world = await SeedWorldAsync();
        var root = await ArtifactRootAsync(world);
        var recordId = await SeedRecordAsync(world, "node.started");
        var before = BlobFiles(root);

        await RollbackAtSidecarWriteAsync(world, recordId);

        var orphan = BlobFiles(root).Except(before).ShouldHaveSingleItem();
        var declaration = (await DeclarationForHolderAsync(recordId)).ShouldNotBeNull();

        await AgeDeclarationAsync(declaration.ArtifactId, TimeSpan.FromDays(30));
        await SweepAsync();
        await AgeQuarantineAsync(declaration.ArtifactId, TimeSpan.FromDays(2));
        await SweepAsync();

        File.Exists(orphan).ShouldBeFalse("a secret-bearing object nothing points at must not outlive the retention windows");
        (await ArtifactAsync(declaration.ArtifactId)).ShouldBeNull();
    }

    [Fact]
    public async Task A_committed_sidecar_still_round_trips_and_its_artifact_reads_as_referenced()
    {
        // The happy path is unchanged: the sidecar commits, the artifact is referenced from a probed oracle site, and
        // the outputs decrypt to exactly what was written.
        var world = await SeedWorldAsync();
        var recordId = await SeedRecordAsync(world, "node.completed");
        var outputs = OversizeOutputs();

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await scope.Resolve<IWorkflowSensitivePayloadStore>().SaveNodeOutputsAsync(recordId, world.WorkflowRunId, world.TeamId, outputs, CancellationToken.None);
            await transaction.CommitAsync();
        }

        using var verify = _fixture.BeginScope();
        var recovered = await verify.Resolve<IWorkflowSensitivePayloadStore>().ReadNodeOutputsAsync(recordId, world.WorkflowRunId, world.TeamId, CancellationToken.None);
        recovered.ShouldNotBeNull();
        recovered["body"].GetString().ShouldBe(outputs["body"].GetString());

        var declaration = (await DeclarationForHolderAsync(recordId)).ShouldNotBeNull();
        var verdict = await verify.Resolve<IArtifactReferenceOracle>().ClassifyAsync(verify.Resolve<CodeSpaceDbContext>(), declaration.ArtifactId, CancellationToken.None);
        verdict.ShouldBe(ArtifactReferenceVerdict.Referenced, "the committed sidecar row is the reference that keeps these bytes forever");
    }

    // ─── The failing write ───────────────────────────────────────────────────

    /// <summary>Drives one save inside a caller-owned transaction that the 0168 trigger aborts, then rolls it back — the production shape at <c>WorkflowEngine.CompleteNodeWithOffloadAsync</c>.</summary>
    private async Task RollbackAtSidecarWriteAsync(World world, Guid recordId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();

        var failure = await Should.ThrowAsync<Exception>(() => scope.Resolve<IWorkflowSensitivePayloadStore>()
            .SaveNodeOutputsAsync(recordId, world.WorkflowRunId, world.TeamId, OversizeOutputs(), CancellationToken.None));

        failure.ToString().ShouldContain("sensitive payload must bind", Case.Sensitive, "the failure must be the real trigger, not an incidental one");

        await transaction.RollbackAsync();
    }

    /// <summary>Outputs whose CIPHERTEXT is comfortably past the inline threshold, so the store routes them through the artifact plane.</summary>
    private static IReadOnlyDictionary<string, JsonElement> OversizeOutputs()
    {
        var body = new string('s', ArtifactStoreConfig.InlineThresholdBytes * 2);

        return new Dictionary<string, JsonElement> { ["body"] = JsonDocument.Parse(JsonSerializer.Serialize(body)).RootElement.Clone() };
    }

    // ─── World + helpers ─────────────────────────────────────────────────────

    /// <summary>The configured blob root, discovered from a real offloaded write rather than from settings, so the test never duplicates the backend's path policy.</summary>
    private async Task<string> ArtifactRootAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        var bytes = new byte[ArtifactStoreConfig.InlineThresholdBytes * 2];
        Random.Shared.NextBytes(bytes);
        var artifactId = await scope.Resolve<IArtifactStore>().PutAsync(world.TeamId, bytes, "application/octet-stream", CancellationToken.None);
        var url = await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking().Where(row => row.Id == artifactId).Select(row => row.StorageUrl).SingleAsync();

        return Directory.GetParent(new Uri(url.ShouldNotBeNull()).LocalPath)!.Parent!.Parent!.FullName;
    }

    private static IReadOnlySet<string> BlobFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToHashSet(StringComparer.Ordinal);

    private async Task<Guid> SeedRecordAsync(World world, string recordType)
    {
        var recordId = Guid.NewGuid();
        const string payload = "{}";
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO workflow_run_record (id, run_id, record_type, payload_json, occurred_at)
            VALUES ({recordId}, {world.WorkflowRunId}, {recordType}, CAST({payload} AS jsonb), clock_timestamp())
            """);

        return recordId;
    }

    private async Task<WorkflowArtifactRetention?> DeclarationForHolderAsync(Guid recordId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifactRetention.AsNoTracking()
            .SingleOrDefaultAsync(row => row.HolderKind == WorkflowSensitivePayloadStore.HolderKind && row.HolderId == recordId);
    }

    private async Task<WorkflowArtifact?> ArtifactAsync(Guid artifactId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifact.AsNoTracking().SingleOrDefaultAsync(row => row.Id == artifactId);
    }

    private async Task<ArtifactRetentionSweepSummary> SweepAsync()
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<IArtifactRetentionReaper>().SweepAsync(CancellationToken.None);
    }

    private async Task AgeDeclarationAsync(Guid artifactId, TimeSpan age)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();

        await db.Database.ExecuteSqlRawAsync("ALTER TABLE workflow_artifact DISABLE TRIGGER workflow_artifact_enforce_immutability");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflow_artifact SET created_at = clock_timestamp() - {age}::interval WHERE id = {artifactId}");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE workflow_artifact ENABLE TRIGGER workflow_artifact_enforce_immutability");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE workflow_artifact_retention
            SET declared_at = clock_timestamp() - {age}::interval, next_sweep_at = clock_timestamp() - {age}::interval, last_modified_at = clock_timestamp()
            WHERE artifact_id = {artifactId}
            """);

        await transaction.CommitAsync();
    }

    private async Task AgeQuarantineAsync(Guid artifactId, TimeSpan age)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE workflow_artifact_retention
            SET quarantined_at = clock_timestamp() - {age}::interval, next_sweep_at = clock_timestamp() - {age}::interval, last_modified_at = clock_timestamp()
            WHERE artifact_id = {artifactId} AND state = 'Quarantined'
            """);
    }

    private async Task<World> SeedWorldAsync()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var workflowRunId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"sensitive-rollback-{actorId:N}@test.local", Name = "Sensitive Rollback" });
        db.Team.Add(new Team { Id = teamId, Slug = $"sensitive-rollback-{teamId:N}", Name = "Sensitive Rollback", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();
        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, WorkflowId = null, SourceType = WorkflowRunSourceTypes.Snapshot,
            ActorType = "user", ActorId = actorId, NormalizedPayloadJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = workflowRunId, WorkflowId = null, WorkflowVersion = null, TeamId = teamId, RunRequestId = requestId,
            SourceType = WorkflowRunSourceTypes.Snapshot, Status = WorkflowRunStatus.Success,
            ScopeRepositoryIds = [], ScopeProjectIds = [], CreatedDate = now, CreatedBy = actorId, LastModifiedBy = actorId,
        });
        await db.SaveChangesAsync();

        return new World(teamId, actorId, workflowRunId);
    }

    private sealed record World(Guid TeamId, Guid ActorId, Guid WorkflowRunId);
}
