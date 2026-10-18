using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.UnitTests.OAuth;

/// <summary>
/// A refresh writes its pair behind the DbContext and then points the caller's entity at it. A context that TRACKS that
/// entity sees the new payload as an unsaved edit, and its next SaveChanges writes it back — over whatever another scope
/// stored since. Two contexts over one in-memory store stand for the two connections: the write the refresher made on a
/// connection of its own, and the scope whose unit of work is still open.
/// </summary>
[Trait("Category", "Unit")]
public class TrackedCredentialEntryTests
{
    private static readonly DateTimeOffset Later = new(2030, 1, 1, 14, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false, "p2")]   // control: without it the open unit of work flushes its copy over the newer pair
    [InlineData(true, "p3")]
    public async Task A_payload_write_made_behind_the_context_is_not_written_back_by_its_next_save(bool markStored, string expectedStoredPayload)
    {
        var store = Guid.NewGuid().ToString();
        var id = await SeedAsync(store, payload: "p0");
        await using var open = NewContext(store);
        var tracked = await open.Credential.SingleAsync(c => c.Id == id);

        // The refresher stores p2 on a connection of its own, then points the entity at it.
        await StoreBehindTheContextAsync(store, id, "p2");
        PointEntityAt(tracked, "p2");
        if (markStored) new TrackedCredentialEntry(open).MarkPayloadStored(tracked);

        // Another run rotates again, and the open unit of work then ends.
        await StoreBehindTheContextAsync(store, id, "p3");
        await open.SaveChangesAsync();

        (await ReadAsync(store, id)).EncryptedPayload.ShouldBe(expectedStoredPayload);
    }

    [Fact]
    public async Task Marking_the_payload_stored_leaves_the_entity_unmodified_to_every_later_look_of_change_detection()
    {
        var store = Guid.NewGuid().ToString();
        var id = await SeedAsync(store, payload: "p0");
        await using var open = NewContext(store);
        var tracked = await open.Credential.SingleAsync(c => c.Id == id);

        await StoreBehindTheContextAsync(store, id, "p2");
        PointEntityAt(tracked, "p2");
        new TrackedCredentialEntry(open).MarkPayloadStored(tracked);

        open.ChangeTracker.DetectChanges();
        open.ChangeTracker.DetectChanges();

        open.Entry(tracked).State.ShouldBe(EntityState.Unchanged, "change detection compares with the snapshot, so clearing only the flag would put the entity back to the consumed token");
        tracked.EncryptedPayload.ShouldBe("p2", "the entity still carries the token later reads in the scope should see");
    }

    [Fact]
    public async Task A_change_the_unit_of_work_really_makes_is_still_saved()
    {
        var store = Guid.NewGuid().ToString();
        var id = await SeedAsync(store, payload: "p0");
        await using var open = NewContext(store);
        var tracked = await open.Credential.SingleAsync(c => c.Id == id);

        await StoreBehindTheContextAsync(store, id, "p2");
        PointEntityAt(tracked, "p2");
        new TrackedCredentialEntry(open).MarkPayloadStored(tracked);

        // A disconnect: the status flips and the token is blanked — on the very columns the refresh just settled.
        tracked.Status = CredentialStatus.Revoked;
        tracked.EncryptedPayload = string.Empty;
        await open.SaveChangesAsync();

        var row = await ReadAsync(store, id);
        row.Status.ShouldBe(CredentialStatus.Revoked);
        row.EncryptedPayload.ShouldBeEmpty();
    }

    [Fact]
    public async Task Edits_to_other_columns_stay_pending_when_the_payload_is_marked_stored()
    {
        var store = Guid.NewGuid().ToString();
        var id = await SeedAsync(store, payload: "p0");
        await using var open = NewContext(store);
        var tracked = await open.Credential.SingleAsync(c => c.Id == id);

        tracked.LastError = "provider said no";
        await StoreBehindTheContextAsync(store, id, "p2");
        PointEntityAt(tracked, "p2");
        new TrackedCredentialEntry(open).MarkPayloadStored(tracked);
        await open.SaveChangesAsync();

        var row = await ReadAsync(store, id);
        row.LastError.ShouldBe("provider said no");
        row.EncryptedPayload.ShouldBe("p2");
    }

    [Fact]
    public async Task An_entity_no_context_tracks_is_left_alone()
    {
        var store = Guid.NewGuid().ToString();
        var id = await SeedAsync(store, payload: "p0");
        var untracked = await ReadAsync(store, id);
        await using var other = NewContext(store);

        PointEntityAt(untracked, "p2");
        new TrackedCredentialEntry(other).MarkPayloadStored(untracked);

        other.ChangeTracker.Entries().ShouldBeEmpty("marking must not start tracking an entity the caller loaded untracked");
        untracked.EncryptedPayload.ShouldBe("p2");
    }

    // ─── Plumbing ──────────────────────────────────────────────────────────────

    private static CodeSpaceDbContext NewContext(string store) => new(new DbContextOptionsBuilder<CodeSpaceDbContext>().UseInMemoryDatabase(store).Options);

    private static async Task<Guid> SeedAsync(string store, string payload)
    {
        await using var db = NewContext(store);
        var credential = new Credential { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), ProviderInstanceId = Guid.NewGuid(), AuthType = AuthType.OAuth, DisplayName = "GitLab · NACHO.L", EncryptedPayload = payload, Status = CredentialStatus.Active };

        db.Credential.Add(credential);
        await db.SaveChangesAsync();

        return credential.Id;
    }

    /// <summary>What <see cref="CredentialPayloadWriter"/>'s UPDATE does: straight to the row, on a connection the context knows nothing of.</summary>
    private static async Task StoreBehindTheContextAsync(string store, Guid id, string payload)
    {
        await using var db = NewContext(store);
        var row = await db.Credential.SingleAsync(c => c.Id == id);

        row.EncryptedPayload = payload;
        row.ExpiresDate = Later;
        await db.SaveChangesAsync();
    }

    /// <summary>What the writer and the refresher's Adopt do to the caller's entity afterwards.</summary>
    private static void PointEntityAt(Credential credential, string payload)
    {
        credential.EncryptedPayload = payload;
        credential.ExpiresDate = Later;
        credential.LastModifiedDate = Later;
        credential.LastModifiedBy = Guid.NewGuid();
    }

    private static async Task<Credential> ReadAsync(string store, Guid id)
    {
        await using var db = NewContext(store);

        return await db.Credential.AsNoTracking().SingleAsync(c => c.Id == id);
    }
}
