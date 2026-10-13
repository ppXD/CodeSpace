using System.Collections.Concurrent;
using System.Text.Json;
using CodeSpace.Api.Extensions.Hangfire;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Enums;
using Hangfire;
using Hangfire.PostgreSql;
using Hangfire.PostgreSql.Factories;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Serilog.Core;
using Serilog.Events;
using Shouldly;

namespace CodeSpace.E2ETests.Storage;

/// <summary>
/// The storage namespace fingerprint backfill, fired the way a worker pod fires it, over a revision a build that stored
/// the PHYSICAL namespace identity wrote.
///
/// <para>Tier: 🟢 High-fidelity (Rule 12) — the real worker host (<c>HangfireHosting=Worker</c>) registers the
/// schedule, and the test triggers THAT registered entry by its id. The tick therefore runs the serialized
/// <c>IJobSafeRunner.Run(jobId, jobType)</c> through a real control-pool server, the job's own DI scope, the mediator
/// pipeline and the production <see cref="IStorageNamespaceFingerprintBackfillService"/> against real Postgres —
/// including the switch of the revision table's immutability trigger. Nothing on the job path is substituted; the
/// host-level substitutions are named on <see cref="RecurringJobWorkerHostFactory"/>.</para>
///
/// <para>Must hold: the physical revision ends holding <see cref="StorageProfileRules.LegacyNamespaceFingerprint"/>,
/// the value the previous build's reaper compares; a revision that already holds it is left alone; and the table is
/// immutable again after the pass. Each run owns a GUID-named database that the fixture drops on every path, and the
/// local root is never created on disk.</para>
///
/// <para>Not covered here: what the previous build's reaper decides over the restored values
/// (<c>ArtifactRetentionReaperFlowTests.PreviousBuild</c>), and the per-pass edge cases — idempotence, concurrent
/// workers (<c>StorageNamespaceFingerprintBackfillFlowTests</c>).</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Worker")]
public sealed class StorageNamespaceFingerprintBackfillE2ETests
{
    /// <summary>The recurring entry's id as Hangfire holds it, spelled out rather than taken from <c>nameof</c>: it is the id a release that deletes the job must remove from the schedule.</summary>
    private const string JobId = "StorageNamespaceFingerprintBackfillRecurringJob";

    private const string ImmutabilityTrigger = "storage_profile_revision_enforce_immutability";

    /// <summary>One tick restores a fresh database's revisions; the slack absorbs a tick the cron scheduler fired at the same moment, and a job that never restores the row fails instead of hanging.</summary>
    private const int MaxTicks = 3;

    /// <summary>Bound on fetch + execution of one tick, sized for a cold CI runner like the sibling smoke test.</summary>
    private static readonly TimeSpan TickBudget = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task A_tick_of_the_registered_job_restores_the_legacy_fingerprint_and_leaves_the_revisions_immutable()
    {
        var sink = new CapturedLogLines();

        await using var factory = new RecurringJobWorkerHostFactory(sink);
        await factory.InitializeAsync();

        _ = factory.Services;   // builds and starts the worker host, which registers every recurring schedule

        var typed = RootConfig($"/srv/codespace-backfill-e2e-{Guid.NewGuid():N}/");
        PhysicalFingerprint(typed).ShouldNotBe(LegacyFingerprint(typed), "fixture check: a trailing separator is where the two identities part, so the seeded row is a real candidate");

        var physical = await SeedRevisionAsync(factory, typed, PhysicalFingerprint(typed));
        var previousBuild = await SeedRevisionAsync(factory, typed, LegacyFingerprint(typed));

        await TickUntilRestoredAsync(factory, sink, physical, LegacyFingerprint(typed));

        (await StoredAsync(factory, physical)).ShouldBe(new Stored(typed, LegacyFingerprint(typed)), "only the fingerprint moves; the configuration stays as written");
        (await StoredAsync(factory, previousBuild)).ShouldBe(new Stored(typed, LegacyFingerprint(typed)), "a revision that already holds the legacy value is not rewritten");

        (await TriggerStateAsync(factory)).ShouldBe("O", $"the pass must switch {ImmutabilityTrigger} back on before it commits; check `SELECT tgenabled FROM pg_trigger WHERE tgname = '{ImmutabilityTrigger}'` ('D' = left disabled)");
        await ShouldRefuseAnUpdateAsync(factory, physical);
    }

    // ─── Ticks ───────────────────────────────────────────────────────────────

    /// <summary>Triggers the registered schedule until the owned row holds <paramref name="expected"/>. Every tick must succeed; the loop is bounded and its failure names the row and the tick.</summary>
    private static async Task TickUntilRestoredAsync(RecurringJobWorkerHostFactory factory, CapturedLogLines sink, Guid revisionId, string expected)
    {
        var storage = OpenStorageOnTheFixtureDatabase(factory);

        for (var tick = 0; tick < MaxTicks; tick++)
        {
            var state = await AwaitTerminalStateAsync(storage, Trigger(storage));

            (state?.Name).ShouldBe(SucceededState.StateName, $"tick {tick + 1} of {JobId} ended '{state?.Name ?? "<none — never fetched>"}'{Cause(state)}. Host warnings: {string.Join(" | ", sink.Lines())}");

            if ((await StoredAsync(factory, revisionId)).Fingerprint == expected) return;
        }

        throw new ShouldAssertException($"revision {revisionId} still lacks the legacy fingerprint after {MaxTicks} successful ticks of {JobId}: the tick ran but restored nothing. Check that the job still sends BackfillStorageNamespaceFingerprintsCommand, then run StorageNamespaceFingerprintBackfillFlowTests, which drive the same service without Hangfire");
    }

    /// <summary>A storage handle the test owns over the fixture's database, with the production options — the same independent observation <c>RecurringJobWorkerSmokeE2ETests</c> makes, rather than the process-wide <c>JobStorage.Current</c>.</summary>
    private static JobStorage OpenStorageOnTheFixtureDatabase(RecurringJobWorkerHostFactory factory)
    {
        var options = HangfireRegistrarBase.BuildStorageOptions();

        return new PostgreSqlStorage(new NpgsqlConnectionFactory(factory.ConnectionString, options), options);
    }

    private static string Trigger(JobStorage storage) =>
        new RecurringJobManager(storage).TriggerJob(JobId)
            ?? throw new ShouldAssertException($"the worker registered no recurring entry '{JobId}'. A renamed job class mints a new id and strands this one; a deleted one must also remove its schedule (ICodeSpaceBackgroundJobClient.RemoveRecurringJobIfExists)");

    /// <summary>Polls the triggered background job until it is terminal or the budget runs out. The state starts unobserved, so an empty read never ends the wait.</summary>
    private static async Task<StateData?> AwaitTerminalStateAsync(JobStorage storage, string backgroundJobId)
    {
        var deadline = DateTime.UtcNow + TickBudget;
        StateData? state = null;

        while (!IsTerminal(state) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval);

            using var connection = storage.GetConnection();
            state = connection.GetStateData(backgroundJobId);
        }

        return state;
    }

    private static bool IsTerminal(StateData? state) =>
        state != null && (state.Name == SucceededState.StateName || state.Name == FailedState.StateName || state.Name == DeletedState.StateName);

    private static string Cause(StateData? state)
    {
        if (state?.Data == null) return string.Empty;

        state.Data.TryGetValue("ExceptionType", out var type);
        state.Data.TryGetValue("ExceptionMessage", out var message);

        return message == null ? $" ({state.Reason})" : $": {type} — {message}";
    }

    // ─── Rows ────────────────────────────────────────────────────────────────

    private sealed record Stored(string Config, string Fingerprint);

    /// <summary>A revision exactly as admission writes one, stamped with <paramref name="fingerprint"/>: inserting is what the immutability trigger allows.</summary>
    private static async Task<Guid> SeedRevisionAsync(RecurringJobWorkerHostFactory factory, string config, string fingerprint)
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"fingerprint-{actorId:N}@test.local", Name = $"fingerprint-{actorId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"fingerprint-{teamId:N}", Name = "Fingerprint Backfill Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        var profile = new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"fingerprint-{profileId:N}", CurrentRevision = 1,
            State = StorageProfileState.Active, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = revisionId, TeamId = teamId, StorageProfileId = profileId, Revision = 1, ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey,
            NonSecretConfigJson = config, NamespaceFingerprint = fingerprint, CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageProfile.Add(profile);
        await db.SaveChangesAsync();

        return revisionId;
    }

    private static async Task<Stored> StoredAsync(RecurringJobWorkerHostFactory factory, Guid revisionId)
    {
        using var scope = factory.Services.CreateScope();

        var row = await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().StorageProfileRevision.AsNoTracking()
            .Where(revision => revision.Id == revisionId).Select(revision => new { revision.NonSecretConfigJson, revision.NamespaceFingerprint }).SingleAsync();

        using var config = JsonDocument.Parse(row.NonSecretConfigJson);
        return new Stored(StorageProfileRules.CanonicalJson(config.RootElement), row.NamespaceFingerprint);
    }

    private static async Task<string> TriggerStateAsync(RecurringJobWorkerHostFactory factory)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT tgenabled::text FROM pg_trigger WHERE tgname = @name", connection);
        command.Parameters.AddWithValue("name", ImmutabilityTrigger);

        return (string)(await command.ExecuteScalarAsync()).ShouldNotBeNull($"trigger {ImmutabilityTrigger} must exist in database {factory.DatabaseName}");
    }

    private static async Task ShouldRefuseAnUpdateAsync(RecurringJobWorkerHostFactory factory, Guid revisionId)
    {
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE storage_profile_revision SET namespace_fingerprint = 'tampered' WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", revisionId);

        var refused = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(), $"revision {revisionId} accepted an UPDATE after the backfill: the pass left {ImmutabilityTrigger} off");
        refused.MessageText.ShouldContain("storage_profile_revision is immutable", Case.Sensitive);
    }

    // ─── Fingerprints ────────────────────────────────────────────────────────

    /// <summary>The canonical text admission persists for a local root.</summary>
    private static string RootConfig(string rootPath)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { rootPath }));
        return StorageProfileRules.CanonicalJson(document.RootElement);
    }

    /// <summary>What a build that stored the physical identity wrote for this configuration.</summary>
    private static string PhysicalFingerprint(string config)
    {
        using var document = JsonDocument.Parse(config);
        return StorageProfileRules.NamespaceFingerprint(new LocalRwxStorageProviderModule(), document.RootElement);
    }

    private static string LegacyFingerprint(string config)
    {
        using var document = JsonDocument.Parse(config);
        return StorageProfileRules.LegacyNamespaceFingerprint(LocalRwxArtifactStorageDriverFactory.TypeKey, document.RootElement);
    }

    /// <summary>Keeps what the host's logger lets through (Warning and above), so a failed tick's message can quote it.</summary>
    private sealed class CapturedLogLines : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();

        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

        public IReadOnlyList<string> Lines() => _events.Select(logEvent => logEvent.RenderMessage()).ToList();
    }
}
