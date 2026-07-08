using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Proves workflow_version is a real immutable release once committed:
///   1. CreateWorkflow populates definition_hash + committed_at on the first version row
///   2. UpdateWorkflow creates a NEW version row with its own hash (doesn't touch the old)
///   3. Direct UPDATE on a committed row throws via the postgres trigger
///   4. Direct DELETE on a committed row throws via the postgres trigger
///   5. Replay-time verification: workflow_version.definition_hash matches the
///      DefinitionHash.Compute() of its current definition_jsonb (no drift)
///
/// The triggers exist as a defense-in-depth measure — even if a bug in application code
/// tries to update a committed version row, the DB layer rejects it. This is the
/// strongest guarantee we can give: a workflow_run's release_hash_at_run can be trusted
/// to be reproducible.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WorkflowVersionImmutabilityTests
{
    private readonly PostgresFixture _fixture;

    public WorkflowVersionImmutabilityTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task CreateWorkflow_populates_definition_hash_and_committed_at_on_first_version()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var def = WorkflowsTestSeed.MinimalDefinition();
        var workflowId = await CreateAsync(teamId, userId, def);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var version = await db.WorkflowVersion.AsNoTracking()
            .SingleAsync(v => v.WorkflowId == workflowId && v.Version == 1);

        version.DefinitionHash.ShouldNotBeNullOrEmpty();
        version.DefinitionHash.Length.ShouldBe(64);
        version.DefinitionHash.ShouldBe(DefinitionHash.Compute(def),
            "definition_hash must equal DefinitionHash.Compute(definition) at INSERT time");
        version.CommittedAt.ShouldNotBeNull("committed_at seals the row — must be set on first INSERT");
    }

    [Fact]
    public async Task UpdateWorkflow_creates_a_new_version_row_with_its_own_hash()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var v1Def = WorkflowsTestSeed.MinimalDefinition();
        var workflowId = await CreateAsync(teamId, userId, v1Def);

        // Edit: add a second downstream node so the hash changes.
        var v2Def = v1Def with
        {
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "mid",   TypeKey = "logic.merge",       Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "end",   TypeKey = "builtin.terminal",  Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() }
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "mid" },
                new() { From = "mid",   To = "end" }
            }
        };

        using (var updateScope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            await updateScope.Resolve<IMediator>().Send(new UpdateWorkflowCommand
            {
                WorkflowId = workflowId,
                Name = "v2",
                Definition = v2Def,
                Activations = new List<WorkflowActivationInput>(),
            });
        }

        using var verifyScope = _fixture.BeginScope();
        var db = verifyScope.Resolve<CodeSpaceDbContext>();
        var versions = await db.WorkflowVersion.AsNoTracking()
            .Where(v => v.WorkflowId == workflowId)
            .OrderBy(v => v.Version)
            .ToListAsync();

        versions.Count.ShouldBe(2, "Update creates a new immutable version row, doesn't overwrite v1");
        versions[0].DefinitionHash.ShouldBe(DefinitionHash.Compute(v1Def));
        versions[1].DefinitionHash.ShouldBe(DefinitionHash.Compute(v2Def));
        versions[0].DefinitionHash.ShouldNotBe(versions[1].DefinitionHash,
            "v2 has different graph → different hash");
    }

    [Fact]
    public async Task Direct_UPDATE_on_committed_row_throws_immutability_exception()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        // Run raw SQL to bypass EF — simulating a bug that attempts to mutate a committed
        // release row directly. The trigger MUST refuse. Using ExecuteSqlInterpolatedAsync
        // so the JSON literal's braces don't collide with ExecuteSqlRawAsync's {n}
        // placeholder syntax.
        var tamperedJson = "{\"tampered\":true}";
        var act = async () => await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE workflow_version SET definition_jsonb = {tamperedJson}::jsonb WHERE workflow_id = {workflowId} AND version = 1");

        var ex = await Should.ThrowAsync<Exception>(act);
        // Postgres surfaces the trigger's RAISE EXCEPTION through Npgsql; the message
        // contains our text. Substring match keeps the test resilient to driver-version
        // wrapping conventions.
        ex.Message.ShouldContain("committed", Case.Insensitive,
            customMessage: "trigger error message must mention 'committed' to be operator-actionable");
        ex.Message.ShouldContain("immutable", Case.Insensitive);
    }

    [Fact]
    public async Task Direct_DELETE_on_committed_row_throws_immutability_exception()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var act = async () => await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM workflow_version WHERE workflow_id = {workflowId} AND version = 1");

        var ex = await Should.ThrowAsync<Exception>(act);
        ex.Message.ShouldContain("committed", Case.Insensitive);
        ex.Message.ShouldContain("immutable", Case.Insensitive);
    }

    [Fact]
    public async Task Stored_definition_hash_matches_recomputed_hash_no_drift()
    {
        // Tamper-detection integrity check: a row's stored definition_hash should always
        // equal DefinitionHash.Compute(parsed definition_jsonb). If they ever diverge,
        // EITHER the hash function changed (a code regression) OR the JSON was mutated
        // bypassing the trigger (a security incident).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var def = WorkflowsTestSeed.MinimalDefinition() with
        {
            Inputs = new[]
            {
                new WorkflowVariable
                {
                    Name = "x",
                    Schema = WorkflowsTestSeed.Json("""{"type":"string"}"""),
                }
            }
        };
        var workflowId = await CreateAsync(teamId, userId, def);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var row = await db.WorkflowVersion.AsNoTracking()
            .SingleAsync(v => v.WorkflowId == workflowId && v.Version == 1);

        var parsed = System.Text.Json.JsonSerializer.Deserialize<WorkflowDefinition>(
            row.DefinitionJson, WorkflowJson.Options)!;
        var recomputed = DefinitionHash.Compute(parsed);

        recomputed.ShouldBe(row.DefinitionHash,
            "definition_hash must equal the canonical hash of the parsed definition — drift is a tamper signal");
    }

    private async Task<Guid> CreateAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "imm-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }
}
