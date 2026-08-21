using CodeSpace.Core.Handlers.QueryHandlers.Workflows;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Workflows;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class GetWorkflowRunIdentityByRefQueryHandlerTests
{
    [Fact]
    public void The_identity_reader_has_no_full_detail_or_artifact_dependencies()
    {
        var constructor = typeof(GetWorkflowRunIdentityByRefQueryHandler).GetConstructors().ShouldHaveSingleItem();

        constructor.GetParameters().Select(parameter => parameter.ParameterType).ShouldBe(new[] { typeof(CodeSpaceDbContext), typeof(ICurrentTeam) });
    }

    [Theory]
    [InlineData("42")]
    [InlineData("11111111-1111-1111-1111-111111111111")]
    public void The_identity_projection_selects_only_identity_columns_without_joining_or_materializing_the_run_graph(string idOrNumber)
    {
        using var db = new CodeSpaceDbContext(new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql("Host=127.0.0.1;Database=unused").UseSnakeCaseNamingConvention().Options);
        var handler = new GetWorkflowRunIdentityByRefQueryHandler(db, new StubTeam(Guid.NewGuid()));

        var sql = handler.BuildQuery(idOrNumber)!.ToQueryString();

        sql.ShouldContain("SELECT w.id AS \"Id\", w.run_number AS \"RunNumber\", w.status AS \"Status\"");
        sql.ShouldContain("FROM workflow_run AS w");
        sql.ShouldNotContain("JOIN");
        sql.ShouldNotContain("outputs_jsonb");
        sql.ShouldNotContain("workflow_run_node");
    }

    [Fact]
    public void Persisted_status_vocabulary_is_database_constrained_and_matches_the_wire_enum()
    {
        var migration = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "backend/src/CodeSpace.Core/Persistence/DbUpFiles/0032_workflow_run_suspended_status.sql"));

        foreach (var status in Enum.GetNames<WorkflowRunStatus>()) migration.ShouldContain($"'{status}'");
        migration.ShouldContain("CHECK (status IN ('Pending','Enqueued','Running','Success','Failure','Cancelled','Suspended'))");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "backend/src"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class StubTeam : ICurrentTeam
    {
        public StubTeam(Guid id) { Id = id; }
        public Guid? Id { get; }
        public bool IsSet => true;
    }
}
