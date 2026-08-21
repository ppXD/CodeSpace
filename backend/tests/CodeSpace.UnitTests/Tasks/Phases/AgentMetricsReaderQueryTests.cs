using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Tasks.Phases;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks.Phases;

/// <summary>
/// Pins the database boundary behind the Workflow Run's agent-card metrics. Result/task envelopes are durable execution
/// carriers and can be arbitrarily large; the live Room/Journal observation must extract only its bounded leaves and
/// must bind every row to the exact team + Workflow Run rather than trusting a caller-provided id list alone.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentMetricsReaderQueryTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=codespace;Username=none;Password=none;Timeout=1;Command Timeout=1";

    [Fact]
    public void Workflow_observation_query_is_exact_scoped_and_never_selects_the_full_json_carriers()
    {
        var sql = WorkflowRowsSql();

        sql.ShouldContain("a.team_id");
        sql.ShouldContain("a.workflow_run_id");
        sql.ShouldContain("ANY");
        sql.ShouldContain("result_jsonb ->", customMessage: "PostgreSQL extracts only the card leaves instead of materializing AgentRunResult");
        sql.ShouldContain("task_jsonb ->", customMessage: "PostgreSQL extracts only the card leaves instead of materializing AgentTask");
        sql.ShouldContain("jsonb_array_length", customMessage: "the full changed-file count is computed without transferring the full array");
        sql.ShouldContain("WITH ORDINALITY", customMessage: "only the first bounded file/stat observations cross the process boundary in stable array order");
        sql.ShouldContain("LIMIT 40");
        sql.ShouldContain("left(", Case.Insensitive, customMessage: "human strings are bounded before crossing the process boundary");
        sql.ShouldNotContain("a.result_jsonb AS", Case.Insensitive);
        sql.ShouldNotContain("a.task_jsonb AS", Case.Insensitive);
        sql.ShouldNotContain("patch", Case.Insensitive);
        sql.ShouldNotContain("transcript", Case.Insensitive);
        sql.ShouldNotContain("summary", Case.Insensitive);
        sql.ShouldNotContain("systemPrompt", Case.Insensitive);
        sql.ShouldNotContain("credential", Case.Insensitive);
    }

    [Fact]
    public void Workflow_tool_count_query_is_scoped_through_the_same_team_and_run()
    {
        var sql = WorkflowToolCountsSql();

        sql.ShouldContain("INNER JOIN agent_run");
        sql.ShouldContain("a.team_id");
        sql.ShouldContain("a.workflow_run_id");
        sql.ShouldContain("ANY");
        sql.ShouldContain("GROUP BY");
    }

    [Fact]
    public async Task Workflow_reader_surfaces_a_database_fault_instead_of_returning_empty_unknown_metrics()
    {
        using var db = Db();
        var reader = new AgentMetricsReader(db);

        var read = () => reader.ReadForWorkflowRunAsync(Guid.NewGuid(), Guid.NewGuid(), new[] { Guid.NewGuid() }, DateTimeOffset.UtcNow, CancellationToken.None);

        await read.ShouldThrowAsync<Exception>();
    }

    private static string WorkflowRowsSql()
    {
        using var db = Db();
        return AgentMetricsReader.WorkflowRowsQuery(db, Guid.NewGuid(), Guid.NewGuid(), new[] { Guid.NewGuid() }).ToQueryString();
    }

    private static string WorkflowToolCountsSql()
    {
        using var db = Db();
        return AgentMetricsReader.WorkflowToolCountsQuery(db, Guid.NewGuid(), Guid.NewGuid(), new[] { Guid.NewGuid() }).ToQueryString();
    }

    private static CodeSpaceDbContext Db() => new(new DbContextOptionsBuilder<CodeSpaceDbContext>()
        .UseNpgsql(UnreachableDatabase).UseSnakeCaseNamingConvention().Options);
}
