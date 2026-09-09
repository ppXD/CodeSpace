using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

[Trait("Category", "Unit")]
public sealed class SupervisorLessonExposureSchemaTests
{
    [Fact]
    public void Exact_exposure_is_a_required_frozen_uuid_array()
    {
        using var db = Infrastructure.EmptyTestDb.New();
        var property = db.Model.FindEntityType(typeof(SupervisorDecisionRecord)).ShouldNotBeNull().FindProperty(nameof(SupervisorDecisionRecord.LessonIds)).ShouldNotBeNull();
        property.IsNullable.ShouldBeFalse();
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, DbUpRunner.ScriptFolder, "0211_supervisor_decision_lesson_exposure.sql"));
        sql.ShouldContain("lesson_ids UUID[] NOT NULL DEFAULT '{}'");
        sql.ShouldContain("NEW.lesson_ids IS DISTINCT FROM OLD.lesson_ids");
        DbUpRunner.DiscoverScriptNames().ShouldContain(name => name.EndsWith("0211_supervisor_decision_lesson_exposure.sql", StringComparison.OrdinalIgnoreCase));
    }
}
