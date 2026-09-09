using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

[Trait("Category", "Unit")]
public sealed class LessonExpirySchemaTests
{
    [Fact]
    public void Expiry_is_required_and_the_migration_backfills_legacy_rows()
    {
        using var db = Infrastructure.EmptyTestDb.New();
        var property = db.Model.FindEntityType(typeof(Lesson)).ShouldNotBeNull().FindProperty(nameof(Lesson.ExpiresAt)).ShouldNotBeNull();
        property.IsNullable.ShouldBeFalse();

        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, DbUpRunner.ScriptFolder, "0210_lesson_expiry.sql"));
        sql.ShouldContain("ADD COLUMN IF NOT EXISTS expires_at");
        sql.ShouldContain("UPDATE lesson SET expires_at = valid_from + INTERVAL '30 days'");
        sql.ShouldContain("ALTER COLUMN expires_at SET NOT NULL");
        sql.ShouldContain("CHECK (expires_at > valid_from)");
        DbUpRunner.DiscoverScriptNames().ShouldContain(name => name.EndsWith("0210_lesson_expiry.sql", StringComparison.OrdinalIgnoreCase));
    }
}
