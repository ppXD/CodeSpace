using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Artifacts;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts;

/// <summary>
/// Declarations settled terminally for a refusal that no longer exists.
///
/// <para>The reaper recorded "this object has more than one placement" as <c>Indeterminate</c> — a terminal keep —
/// and its claim admits only <c>Declared</c> and <c>Quarantined</c>, so nothing ever looked at those rows again. Now
/// that a purge can name which placement it means, the refusal is gone and the rows must come back.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ReopenMultiLocationRetentionTests
{
    private const string ReopenedCode = "artifact-routed-multiple-locations";

    private readonly PostgresFixture _fixture;

    public ReopenMultiLocationRetentionTests(PostgresFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(true, ArtifactRetentionState.Quarantined)]   // already past its first sweep
    [InlineData(false, ArtifactRetentionState.Declared)]     // stopped before it
    public async Task A_declaration_stopped_by_the_old_refusal_returns_to_the_state_it_was_in(bool quarantined, ArtifactRetentionState expected)
    {
        // The state each row returns to is not a detail: ck_workflow_artifact_retention_state forbids Declared with a
        // quarantined_at and requires one for Quarantined, so reopening every row to one state fails half of them.
        var artifactId = await SettledTerminalAsync(ReopenedCode, quarantined);

        await ReopenAsync();

        var row = await RetentionAsync(artifactId);
        row.State.ShouldBe(expected);
        row.TerminalAt.ShouldBeNull("a row that is claimable again is not terminal");
        row.AttemptCount.ShouldBe(0, "the attempts it spent were spent against a refusal that no longer exists");
    }

    [Fact]
    public async Task Every_other_terminal_reason_is_left_alone()
    {
        // Indeterminate is the reaper's honest "I cannot establish this", and almost every reason for it is still
        // true. Reopening rows indiscriminately would put artifacts back in front of a collector that already
        // decided, correctly, that it must not touch them.
        var stillTrue = await SettledTerminalAsync("artifact-reference-unreadable", quarantined: false);

        await ReopenAsync();

        var row = await RetentionAsync(stillTrue);
        row.State.ShouldBe(ArtifactRetentionState.Indeterminate);
        row.TerminalAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_reopened_declaration_is_visible_to_the_sweep_again()
    {
        // The point of reopening is not the column values — it is that the row is claimable. A row the claim query
        // still filters out has been rewritten and left exactly as stuck as it was.
        var artifactId = await SettledTerminalAsync(ReopenedCode, quarantined: false);

        await ReopenAsync();

        using var scope = _fixture.BeginScope();
        var claimable = await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifactRetention.AsNoTracking()
            .Where(row => row.ArtifactId == artifactId
                && (row.State == ArtifactRetentionState.Declared || row.State == ArtifactRetentionState.Quarantined)
                && row.NextSweepAt <= DateTimeOffset.UtcNow && row.OwnerId == null)
            .CountAsync();

        claimable.ShouldBe(1);
    }

    // ─── World ───────────────────────────────────────────────────────────────

    /// <summary>Runs the migration's statement, so the test exercises the shipped SQL rather than a paraphrase of it.</summary>
    private async Task ReopenAsync()
    {
        using var scope = _fixture.BeginScope();
        var sql = await File.ReadAllTextAsync(MigrationPath());

        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlRawAsync(sql);
    }

    private static string MigrationPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "backend", "src"))) directory = directory.Parent;

        directory.ShouldNotBeNull("the repository root must be reachable from the test output directory");

        return Path.Combine(directory.FullName, "backend", "src", "CodeSpace.Core", "Persistence", "DbUpFiles", "0176_reopen_multi_location_retention.sql");
    }

    private async Task<WorkflowArtifactRetention> RetentionAsync(Guid artifactId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowArtifactRetention.AsNoTracking().SingleAsync(row => row.ArtifactId == artifactId);
    }

    private async Task<Guid> SettledTerminalAsync(string errorCode, bool quarantined)
    {
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var artifactId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var payload = System.Text.Encoding.UTF8.GetBytes($"stuck {artifactId:N}");
        db.WorkflowArtifact.Add(new WorkflowArtifact
        {
            Id = artifactId, TeamId = teamId, Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload)),
            ContentType = "application/octet-stream", SizeBytes = payload.Length, InlineBytes = payload, CreatedAt = now,
        });
        db.WorkflowArtifactRetention.Add(new WorkflowArtifactRetention
        {
            ArtifactId = artifactId, TeamId = teamId, RetentionClass = nameof(ArtifactRetentionClass.ArtifactManifestContent),
            HolderKind = "artifact_manifest", HolderId = actorId, State = ArtifactRetentionState.Indeterminate,
            DeclaredAt = now.AddDays(-40), QuarantinedAt = quarantined ? now.AddDays(-3) : null,
            NextSweepAt = now.AddDays(-1), TerminalAt = now.AddDays(-1), AttemptCount = 3,
            LastErrorCode = errorCode, Revision = 4, LastModifiedAt = now,
        });
        await db.SaveChangesAsync();

        return artifactId;
    }
}
