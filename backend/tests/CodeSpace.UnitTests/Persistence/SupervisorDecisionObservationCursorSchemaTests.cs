using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

[Trait("Category", "Unit")]
public sealed class SupervisorDecisionObservationCursorSchemaTests
{
    [Fact]
    public void Cursor_columns_are_database_generated_and_existing_sequence_remains_unchanged()
    {
        using var db = new CodeSpaceDbContext(new DbContextOptionsBuilder<CodeSpaceDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused")
            .UseSnakeCaseNamingConvention().Options);
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(SupervisorDecisionRecord));

        entity.ShouldNotBeNull();
        var storyOrder = entity.FindProperty("StoryOrder");
        storyOrder.ShouldNotBeNull();
        storyOrder.ValueGenerated.ShouldBe(ValueGenerated.OnAdd);
        storyOrder.GetBeforeSaveBehavior().ShouldBe(PropertySaveBehavior.Ignore);
        storyOrder.GetAfterSaveBehavior().ShouldBe(PropertySaveBehavior.Throw);
        var observationRevision = entity.FindProperty("ObservationRevision");
        observationRevision.ShouldNotBeNull();
        observationRevision.ValueGenerated.ShouldBe(ValueGenerated.OnAddOrUpdate);
        observationRevision.GetBeforeSaveBehavior().ShouldBe(PropertySaveBehavior.Ignore);
        observationRevision.GetAfterSaveBehavior().ShouldBe(PropertySaveBehavior.Ignore);
        entity.FindProperty(nameof(SupervisorDecisionRecord.Sequence))!.ValueGenerated.ShouldBe(ValueGenerated.OnAdd,
            "the additive observation cursor must not change the existing replay Sequence contract");
    }

    [Fact]
    public void Migration_admits_after_the_run_lock_and_labels_legacy_backfill_honestly()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles", "0161_supervisor_decision_observation_cursor.sql"));

        sql.ShouldContain("pg_advisory_xact_lock");
        sql.IndexOf("pg_advisory_xact_lock", StringComparison.Ordinal).ShouldBeLessThan(sql.IndexOf("nextval('supervisor_decision_story_order_seq'", StringComparison.Ordinal));
        sql.ShouldContain("SET story_order = sequence");
        sql.ShouldContain("legacy allocation order");
        sql.ShouldContain("never reconstructed commit order");
        sql.ShouldContain("NEW.observation_revision := nextval('supervisor_decision_observation_revision_seq'");
        sql.ShouldContain("NEW.story_order IS DISTINCT FROM OLD.story_order");
        sql.ShouldNotContain("ALTER COLUMN sequence");
    }
}
