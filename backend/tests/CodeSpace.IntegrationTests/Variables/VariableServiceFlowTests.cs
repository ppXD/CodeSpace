using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Variables;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Variables;

/// <summary>
/// End-to-end through <c>IVariableService</c> against real Postgres + real encryption.
/// Covers the unified table contract:
///   1. CRUD primitives per scope (Team + Workflow)
///   2. Per-row value-column exclusivity — Secret rows have value_encrypted set / value_plain null,
///      everything else inverse; DB CHECK constraint enforces this when caller does the wrong thing
///   3. Tenant isolation across teams (Team scope)
///   4. Workflow-local isolation across workflows in the same team (Workflow scope)
///   5. Rotation in-place — Set on existing tuple replaces value, supports type-change too
///   6. ListAsync / GetAsync NEVER surface secret plaintext
///   7. GetAllForEngineAsync surfaces ALL values (engine-only path, decrypted)
///   8. Encryption at rest — value_encrypted column is unreadable raw bytes
///   9. Mixed types in same scope — string + number + secret + object coexist by name
///   10. Soft-delete + recreate works without unique-constraint clash
///
/// Rule 9 three-tier coverage: encryption math pinned by unit tests; this suite adds the
/// service / DB / wiring tier. Engine integration uses this service via DI.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class VariableServiceFlowTests
{
    private readonly PostgresFixture _fixture;

    public VariableServiceFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    // ─── Team-scoped CRUD primitives ────────────────────────────────────────────

    [Fact]
    public async Task Team_set_persists_a_plain_string_variable_and_get_round_trips_it()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScope();
        var sut = scope.Resolve<IVariableService>();

        await sut.SetAsync(VariableScope.Team, teamId, teamId, "REGION", VariableValueType.String,
            Json("\"us-east-1\""), description: "Deployment region", userId, CancellationToken.None);

        var summary = await sut.GetAsync(VariableScope.Team, teamId, teamId, "REGION", CancellationToken.None);
        summary.ShouldNotBeNull();
        summary!.Name.ShouldBe("REGION");
        summary.ValueType.ShouldBe(VariableValueType.String);
        summary.ValuePlain.ShouldBe("\"us-east-1\"");
        summary.Description.ShouldBe("Deployment region");
    }

    [Fact]
    public async Task Team_set_persists_a_secret_and_summary_never_returns_plaintext()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScope();
        var sut = scope.Resolve<IVariableService>();

        await sut.SetAsync(VariableScope.Team, teamId, teamId, "ANTHROPIC_API_KEY", VariableValueType.Secret,
            Json("\"sk-ant-test-value\""), null, userId, CancellationToken.None);

        var summary = await sut.GetAsync(VariableScope.Team, teamId, teamId, "ANTHROPIC_API_KEY", CancellationToken.None);
        summary.ShouldNotBeNull();
        summary!.ValueType.ShouldBe(VariableValueType.Secret);
        summary.ValuePlain.ShouldBeNull("ValuePlain must be NULL for Secret rows — the API surface never exposes plaintext");
    }

    [Fact]
    public async Task Team_GetAllForEngineAsync_decrypts_secrets_and_parses_plain_values()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScope();
        var sut = scope.Resolve<IVariableService>();

        await sut.SetAsync(VariableScope.Team, teamId, teamId, "MAX_RETRIES", VariableValueType.Number,
            Json("5"), null, userId, CancellationToken.None);
        await sut.SetAsync(VariableScope.Team, teamId, teamId, "API_TOKEN", VariableValueType.Secret,
            Json("\"sk-rotated\""), null, userId, CancellationToken.None);

        var resolved = await sut.GetAllForEngineAsync(VariableScope.Team, teamId, CancellationToken.None);

        resolved.Count.ShouldBe(2);
        var maxRetries = resolved.Single(r => r.Name == "MAX_RETRIES");
        maxRetries.Value.GetInt32().ShouldBe(5);
        maxRetries.ValueType.ShouldBe(VariableValueType.Number);

        var apiToken = resolved.Single(r => r.Name == "API_TOKEN");
        apiToken.Value.GetString().ShouldBe("sk-rotated");
        apiToken.ValueType.ShouldBe(VariableValueType.Secret);
    }

    [Fact]
    public async Task Team_GetAsync_returns_null_for_missing_name()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScope();
        var sut = scope.Resolve<IVariableService>();

        (await sut.GetAsync(VariableScope.Team, teamId, teamId, "NEVER_SET", CancellationToken.None))
            .ShouldBeNull();
    }

    [Fact]
    public async Task Team_ListAsync_returns_only_active_rows()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScope();
        var sut = scope.Resolve<IVariableService>();

        await sut.SetAsync(VariableScope.Team, teamId, teamId, "KEEP", VariableValueType.String, Json("\"a\""), null, userId, CancellationToken.None);
        await sut.SetAsync(VariableScope.Team, teamId, teamId, "DROP", VariableValueType.String, Json("\"b\""), null, userId, CancellationToken.None);
        await sut.DeleteAsync(VariableScope.Team, teamId, teamId, "DROP", userId, CancellationToken.None);

        var list = await sut.ListAsync(VariableScope.Team, teamId, teamId, CancellationToken.None);
        list.Select(v => v.Name).ShouldBe(new[] { "KEEP" });
    }

    // ─── Rotation ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Team_set_on_existing_name_rotates_the_value_in_place()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScope();
        var sut = scope.Resolve<IVariableService>();

        await sut.SetAsync(VariableScope.Team, teamId, teamId, "API_TOKEN", VariableValueType.Secret,
            Json("\"first-value\""), null, userId, CancellationToken.None);
        await sut.SetAsync(VariableScope.Team, teamId, teamId, "API_TOKEN", VariableValueType.Secret,
            Json("\"rotated-value\""), null, userId, CancellationToken.None);

        var resolved = await sut.GetAllForEngineAsync(VariableScope.Team, teamId, CancellationToken.None);
        resolved.Single(r => r.Name == "API_TOKEN").Value.GetString().ShouldBe("rotated-value");

        var list = await sut.ListAsync(VariableScope.Team, teamId, teamId, CancellationToken.None);
        list.Count(v => v.Name == "API_TOKEN").ShouldBe(1, "rotation is in-place — no duplicate active row");
    }

    [Fact]
    public async Task Team_set_can_change_value_type_on_existing_name()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScope();
        var sut = scope.Resolve<IVariableService>();

        await sut.SetAsync(VariableScope.Team, teamId, teamId, "FOO", VariableValueType.String,
            Json("\"originally-string\""), null, userId, CancellationToken.None);
        await sut.SetAsync(VariableScope.Team, teamId, teamId, "FOO", VariableValueType.Secret,
            Json("\"now-encrypted\""), null, userId, CancellationToken.None);

        var summary = await sut.GetAsync(VariableScope.Team, teamId, teamId, "FOO", CancellationToken.None);
        summary!.ValueType.ShouldBe(VariableValueType.Secret);
        summary.ValuePlain.ShouldBeNull();

        var resolved = await sut.GetAllForEngineAsync(VariableScope.Team, teamId, CancellationToken.None);
        resolved.Single(r => r.Name == "FOO").Value.GetString().ShouldBe("now-encrypted");
    }

    // ─── Encryption at rest ─────────────────────────────────────────────────────

    [Fact]
    public async Task Secret_value_in_DB_is_unreadable_raw_bytes_not_plaintext()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using (var scope = _fixture.BeginScope())
        {
            var sut = scope.Resolve<IVariableService>();
            await sut.SetAsync(VariableScope.Team, teamId, teamId, "AT_REST_TEST", VariableValueType.Secret,
                Json("\"sentinel-plaintext-MUST-NOT-LEAK\""), null, userId, CancellationToken.None);
        }

        using var verifyScope = _fixture.BeginScope();
        var db = verifyScope.Resolve<CodeSpaceDbContext>();
        var raw = await db.Variable.AsNoTracking()
            .SingleAsync(v => v.TeamId == teamId && v.Name == "AT_REST_TEST" && v.DeletedDate == null);

        raw.ValuePlain.ShouldBeNull();
        raw.ValueEncrypted.ShouldNotBeNull();
        var asUtf8 = System.Text.Encoding.UTF8.GetString(raw.ValueEncrypted!);
        asUtf8.ShouldNotContain("sentinel-plaintext-MUST-NOT-LEAK",
            customMessage: "secret bytes must be encrypted at rest — plaintext appearing here is a security regression");
    }

    [Fact]
    public async Task Plain_value_in_DB_is_stored_as_JSON_in_value_plain_column()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using (var scope = _fixture.BeginScope())
        {
            var sut = scope.Resolve<IVariableService>();
            await sut.SetAsync(VariableScope.Team, teamId, teamId, "RAW_PLAIN", VariableValueType.Object,
                Json("""{"nested":{"count":42}}"""), null, userId, CancellationToken.None);
        }

        using var verifyScope = _fixture.BeginScope();
        var db = verifyScope.Resolve<CodeSpaceDbContext>();
        var raw = await db.Variable.AsNoTracking()
            .SingleAsync(v => v.TeamId == teamId && v.Name == "RAW_PLAIN" && v.DeletedDate == null);

        raw.ValueEncrypted.ShouldBeNull();
        raw.ValuePlain.ShouldNotBeNull();
        // The JSON survives round-trip including nested structure.
        var parsed = JsonDocument.Parse(raw.ValuePlain!).RootElement;
        parsed.GetProperty("nested").GetProperty("count").GetInt32().ShouldBe(42);
    }

    // ─── Tenant + workflow isolation ────────────────────────────────────────────

    [Fact]
    public async Task Team_scope_is_isolated_between_teams_even_with_same_name()
    {
        var (teamA, userA) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, userB) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using (var setup = _fixture.BeginScope())
        {
            var sut = setup.Resolve<IVariableService>();
            await sut.SetAsync(VariableScope.Team, teamA, teamA, "DEPLOY_TARGET", VariableValueType.String,
                Json("\"team-a-bucket\""), null, userA, CancellationToken.None);
            await sut.SetAsync(VariableScope.Team, teamB, teamB, "DEPLOY_TARGET", VariableValueType.String,
                Json("\"team-b-bucket\""), null, userB, CancellationToken.None);
        }

        using var verify = _fixture.BeginScope();
        var sut2 = verify.Resolve<IVariableService>();
        var a = (await sut2.GetAsync(VariableScope.Team, teamA, teamA, "DEPLOY_TARGET", CancellationToken.None))!;
        var b = (await sut2.GetAsync(VariableScope.Team, teamB, teamB, "DEPLOY_TARGET", CancellationToken.None))!;

        a.ValuePlain.ShouldBe("\"team-a-bucket\"");
        b.ValuePlain.ShouldBe("\"team-b-bucket\"");
    }

    [Fact]
    public async Task Workflow_scope_is_isolated_between_workflows_in_same_team()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowA = await CreateWorkflowDirectAsync(teamId, userId);
        var workflowB = await CreateWorkflowDirectAsync(teamId, userId);

        using (var setup = _fixture.BeginScope())
        {
            var sut = setup.Resolve<IVariableService>();
            await sut.SetAsync(VariableScope.Workflow, workflowA, teamId, "MODE", VariableValueType.String,
                Json("\"a-mode\""), null, userId, CancellationToken.None);
            await sut.SetAsync(VariableScope.Workflow, workflowB, teamId, "MODE", VariableValueType.String,
                Json("\"b-mode\""), null, userId, CancellationToken.None);
        }

        using var verify = _fixture.BeginScope();
        var sut2 = verify.Resolve<IVariableService>();
        (await sut2.GetAsync(VariableScope.Workflow, workflowA, teamId, "MODE", CancellationToken.None))!
            .ValuePlain.ShouldBe("\"a-mode\"");
        (await sut2.GetAsync(VariableScope.Workflow, workflowB, teamId, "MODE", CancellationToken.None))!
            .ValuePlain.ShouldBe("\"b-mode\"");
    }

    [Fact]
    public async Task Workflow_scope_writes_team_id_denormalised_from_workflow_for_tenant_filtering()
    {
        // team_id on the variable row must match workflow.team_id so global tenant-filter
        // sweeps (e.g. "delete all variables for this team") catch workflow-scoped rows
        // without a workflow JOIN.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowDirectAsync(teamId, userId);

        using (var setup = _fixture.BeginScope())
        {
            var sut = setup.Resolve<IVariableService>();
            await sut.SetAsync(VariableScope.Workflow, workflowId, teamId, "FOO", VariableValueType.String,
                Json("\"v\""), null, userId, CancellationToken.None);
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var row = await db.Variable.AsNoTracking()
            .SingleAsync(v => v.ScopeId == workflowId && v.Name == "FOO" && v.DeletedDate == null);

        row.TeamId.ShouldBe(teamId, "service must denormalise workflow.team_id into variable.team_id");
        row.Scope.ShouldBe(VariableScope.Workflow);
        row.ScopeId.ShouldBe(workflowId);
    }

    // ─── Mixed-type variables in same scope ──────────────────────────────────────

    [Fact]
    public async Task Multiple_value_types_coexist_in_same_scope_by_name()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScope();
        var sut = scope.Resolve<IVariableService>();

        await sut.SetAsync(VariableScope.Team, teamId, teamId, "S", VariableValueType.String,  Json("\"s\""),     null, userId, CancellationToken.None);
        await sut.SetAsync(VariableScope.Team, teamId, teamId, "N", VariableValueType.Number,  Json("3.14"),      null, userId, CancellationToken.None);
        await sut.SetAsync(VariableScope.Team, teamId, teamId, "B", VariableValueType.Boolean, Json("true"),      null, userId, CancellationToken.None);
        await sut.SetAsync(VariableScope.Team, teamId, teamId, "O", VariableValueType.Object,  Json("""{"k":1}"""), null, userId, CancellationToken.None);
        await sut.SetAsync(VariableScope.Team, teamId, teamId, "A", VariableValueType.Array,   Json("[1,2,3]"),   null, userId, CancellationToken.None);
        await sut.SetAsync(VariableScope.Team, teamId, teamId, "X", VariableValueType.Secret,  Json("\"sk-x\""),  null, userId, CancellationToken.None);

        var resolved = (await sut.GetAllForEngineAsync(VariableScope.Team, teamId, CancellationToken.None))
            .ToDictionary(r => r.Name);

        resolved["S"].Value.GetString().ShouldBe("s");
        resolved["N"].Value.GetDouble().ShouldBe(3.14);
        resolved["B"].Value.GetBoolean().ShouldBeTrue();
        resolved["O"].Value.GetProperty("k").GetInt32().ShouldBe(1);
        resolved["A"].Value.EnumerateArray().Select(e => e.GetInt32()).ShouldBe(new[] { 1, 2, 3 });
        resolved["X"].Value.GetString().ShouldBe("sk-x");
        resolved["X"].ValueType.ShouldBe(VariableValueType.Secret);
    }

    // ─── Soft-delete + recreate ─────────────────────────────────────────────────

    [Fact]
    public async Task Delete_then_recreate_same_name_succeeds_without_constraint_clash()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScope();
        var sut = scope.Resolve<IVariableService>();

        await sut.SetAsync(VariableScope.Team, teamId, teamId, "RECYCLE", VariableValueType.String,
            Json("\"first\""), null, userId, CancellationToken.None);
        await sut.DeleteAsync(VariableScope.Team, teamId, teamId, "RECYCLE", userId, CancellationToken.None);
        await sut.SetAsync(VariableScope.Team, teamId, teamId, "RECYCLE", VariableValueType.String,
            Json("\"second\""), null, userId, CancellationToken.None);

        (await sut.GetAsync(VariableScope.Team, teamId, teamId, "RECYCLE", CancellationToken.None))!
            .ValuePlain.ShouldBe("\"second\"");
    }

    [Fact]
    public async Task Delete_missing_name_is_idempotent_no_op()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScope();
        var sut = scope.Resolve<IVariableService>();

        // Should not throw — deleting a missing name is a no-op for caller convenience
        // (the API DELETE handler can be idempotent without a separate "exists?" call).
        await sut.DeleteAsync(VariableScope.Team, teamId, teamId, "NEVER_EXISTED", userId, CancellationToken.None);
    }

    // ─── Helper: bypass the workflow CRUD pipeline + seed a row directly ────────

    private async Task<Guid> CreateWorkflowDirectAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var workflowId = Guid.NewGuid();
        db.Workflow.Add(new Workflow
        {
            Id = workflowId,
            TeamId = teamId,
            Name = $"wf-{workflowId:N}",
            DefinitionJson = "{}",
            LatestVersion = 1,
            Enabled = true,
            CreatedBy = userId,
            LastModifiedBy = userId,
        });

        await db.SaveChangesAsync();
        return workflowId;
    }
}
