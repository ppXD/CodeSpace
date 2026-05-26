using Autofac;
using CodeSpace.Messages.Constants;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

[Collection(PostgresCollection.Name)]
public class AuditingTests
{
    private readonly PostgresFixture _fixture;

    public AuditingTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Audit_columns_filled_on_add()
    {
        var before = DateTimeOffset.UtcNow;
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var user = new User { Id = Guid.NewGuid(), Email = UniqueEmail("audit-add"), Name = "Audit Add" };
        db.User.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var saved = await db.User.AsNoTracking().SingleAsync(u => u.Id == user.Id).ConfigureAwait(false);
        saved.CreatedDate.ShouldBeGreaterThanOrEqualTo(before);
        saved.CreatedBy.ShouldBe(SystemUsers.SeederId);
        saved.LastModifiedDate.ShouldBe(saved.CreatedDate);
        saved.LastModifiedBy.ShouldBe(saved.CreatedBy);
    }

    [Fact]
    public async Task Update_changes_only_last_modified_columns()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var user = new User { Id = Guid.NewGuid(), Email = UniqueEmail("audit-mod"), Name = "Original" };
        db.User.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var originalCreatedDate = user.CreatedDate;
        var originalCreatedBy = user.CreatedBy;

        await Task.Delay(50).ConfigureAwait(false);

        user.Name = "Updated";
        await db.SaveChangesAsync().ConfigureAwait(false);

        var reloaded = await db.User.AsNoTracking().SingleAsync(u => u.Id == user.Id).ConfigureAwait(false);
        reloaded.CreatedDate.ShouldBe(originalCreatedDate);
        reloaded.CreatedBy.ShouldBe(originalCreatedBy);
        reloaded.LastModifiedDate.ShouldBeGreaterThan(originalCreatedDate);
    }

    [Fact]
    public async Task Update_ignores_attempted_tampering_of_created_columns()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var user = new User { Id = Guid.NewGuid(), Email = UniqueEmail("audit-tamper"), Name = "Original" };
        db.User.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var originalCreatedBy = user.CreatedBy;
        var originalCreatedDate = user.CreatedDate;

        user.CreatedBy = Guid.Parse("DEADBEEF-0000-0000-0000-000000000001");
        user.CreatedDate = DateTimeOffset.UtcNow.AddYears(-1);
        user.Name = "Updated";
        await db.SaveChangesAsync().ConfigureAwait(false);

        var reloaded = await db.User.AsNoTracking().SingleAsync(u => u.Id == user.Id).ConfigureAwait(false);
        reloaded.CreatedBy.ShouldBe(originalCreatedBy);
        reloaded.CreatedDate.ShouldBe(originalCreatedDate);
    }

    [Fact]
    public async Task Audit_falls_back_to_seeder_user_when_no_http_context()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var user = new User { Id = Guid.NewGuid(), Email = UniqueEmail("audit-fallback"), Name = "Fallback" };
        db.User.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var saved = await db.User.AsNoTracking().SingleAsync(u => u.Id == user.Id).ConfigureAwait(false);
        saved.CreatedBy.ShouldBe(SystemUsers.SeederId);
    }

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test";
}
