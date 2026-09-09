using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

[Trait("Category", "Unit")]
public sealed class LessonRuntimeApplicabilitySchemaTests
{
    [Fact]
    public void Entity_and_migration_keep_legacy_lessons_generic_and_bound_selector_counts()
    {
        using var db = Infrastructure.EmptyTestDb.New();
        var entity = db.Model.FindEntityType(typeof(Lesson)).ShouldNotBeNull();

        foreach (var property in new[] { nameof(Lesson.ApplicableModels), nameof(Lesson.ApplicableHarnesses), nameof(Lesson.RequiredTools) })
            entity.FindProperty(property).ShouldNotBeNull().IsNullable.ShouldBeFalse();

        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles", "0214_lesson_runtime_applicability.sql"));
        sql.ShouldContain("NOT NULL DEFAULT '{}'");
        sql.ShouldContain("cardinality(applicable_models) <= 20");
        sql.ShouldContain("cardinality(applicable_harnesses) <= 20");
        sql.ShouldContain("cardinality(required_tools) <= 20");
        DbUpRunner.DiscoverScriptNames().ShouldContain(name => name.EndsWith("0214_lesson_runtime_applicability.sql", StringComparison.OrdinalIgnoreCase));
    }
}
