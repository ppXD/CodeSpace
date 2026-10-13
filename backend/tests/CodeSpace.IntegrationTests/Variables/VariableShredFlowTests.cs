using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Variables;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Projects;
using CodeSpace.Messages.Commands.Variables;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Variables;

/// <summary>
/// A deleted variable keeps its row for the audit trail and nothing a secret could be recovered from. Every read of a
/// variable filters on <c>deleted_date IS NULL</c> and nothing restores one, so a deleted Secret's ciphertext could only
/// ever be opened by someone holding a copy of the database and the master key. Both ways a variable is deleted — on its
/// own, and with the project it belongs to — drop it, and 0249's check admits a NULL ciphertext on a deleted Secret only.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class VariableShredFlowTests
{
    private readonly PostgresFixture _fixture;

    public VariableShredFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData(VariableValueType.Secret, "\"CANARY-variable-secret\"")]
    [InlineData(VariableValueType.String, "\"plain-kept\"")]
    public async Task Deleting_a_variable_drops_a_secrets_ciphertext_and_the_name_is_free_again(VariableValueType valueType, string value)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScope();
        var variables = scope.Resolve<IVariableService>();

        await variables.SetAsync(VariableScope.Team, teamId, teamId, "DEPLOY_TOKEN", valueType, Json(value), null, userId, CancellationToken.None);
        await variables.DeleteAsync(VariableScope.Team, teamId, teamId, "DEPLOY_TOKEN", userId, CancellationToken.None);

        var deleted = await DeletedRowAsync(VariableScope.Team, teamId, "DEPLOY_TOKEN");
        deleted.ValueEncrypted.ShouldBeNull("a deleted variable must hold no ciphertext anyone could open later");
        deleted.ValuePlain.ShouldBe(valueType == VariableValueType.Secret ? null : value, "a plain value is no secret and stays as the record of what the variable held");

        await variables.SetAsync(VariableScope.Team, teamId, teamId, "DEPLOY_TOKEN", valueType, Json(value), null, userId, CancellationToken.None);
        (await variables.GetAllForEngineAsync(VariableScope.Team, teamId, CancellationToken.None)).Single(v => v.Name == "DEPLOY_TOKEN").Value.GetRawText().ShouldBe(value, "the name is free again and the new row reads back");
    }

    [Fact]
    public async Task Deleting_a_project_drops_the_ciphertext_of_its_secret_variables()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        Guid projectId;

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            projectId = await mediator.Send(new CreateProjectCommand { Name = $"Shred {Guid.NewGuid():N}" });
            await mediator.Send(new SetProjectVariableCommand { ProjectId = projectId, Name = "API_KEY", ValueType = VariableValueType.Secret, Value = Json("\"CANARY-project-secret\"") });
        }

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            await scope.Resolve<IMediator>().Send(new DeleteProjectCommand { ProjectId = projectId });

        (await DeletedRowAsync(VariableScope.Project, projectId, "API_KEY")).ValueEncrypted.ShouldBeNull("the project cascade retires its variables the same way a delete does");
    }

    [Fact]
    public async Task A_live_secret_still_must_carry_its_ciphertext()
    {
        // 0249 relaxed the value check for deleted rows only. A live Secret with no ciphertext is still a programmer
        // error the database refuses — otherwise the engine would read NULL where a secret should be.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IVariableService>().SetAsync(VariableScope.Team, teamId, teamId, "LIVE_SECRET", VariableValueType.Secret, Json("\"CANARY-live\""), null, userId, CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var refused = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE variable SET value_encrypted = NULL WHERE team_id = {teamId} AND name = 'LIVE_SECRET' AND deleted_date IS NULL"));

        refused.ConstraintName.ShouldBe("chk_variable_value_exclusive");
    }

    private async Task<CodeSpace.Core.Persistence.Entities.Variable> DeletedRowAsync(VariableScope scope, Guid scopeId, string name)
    {
        using var verify = _fixture.BeginScope();

        return await verify.Resolve<CodeSpaceDbContext>().Variable.AsNoTracking().SingleAsync(v => v.Scope == scope && v.ScopeId == scopeId && v.Name == name && v.DeletedDate != null);
    }

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();
}
