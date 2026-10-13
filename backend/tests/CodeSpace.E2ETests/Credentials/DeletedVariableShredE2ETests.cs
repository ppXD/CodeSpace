using System.Net;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Credentials;

/// <summary>
/// DELETE /api/team-variables/{name} on a Secret leaves its row for the audit trail and no ciphertext in it. Before, the
/// row kept its AES-GCM envelope forever, so a deleted secret stayed recoverable from any database copy by whoever held
/// the variable master key.
///
/// <para>Driven through the real HTTP surface (PUT, DELETE and GET /api/team-variables), the real mediator pipeline and
/// its transaction, real encryption and Postgres. Fidelity: HIGH — nothing is stubbed.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class DeletedVariableShredE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TenantHarness _tenants;

    public DeletedVariableShredE2ETests(TaskLaunchApiFactory factory) { _tenants = new TenantHarness(factory); }

    [Fact]
    public async Task Deleting_a_secret_team_variable_leaves_its_row_without_ciphertext_and_frees_the_name()
    {
        var owner = await _tenants.AccountAsync();
        var team = await _tenants.TeamAsync((owner, TeamRole.Owner));
        var body = new { name = "DEPLOY_TOKEN", valueType = "Secret", value = $"CANARY-deleted-variable-{Guid.NewGuid():N}" };

        await ShouldSucceedAsync(await _tenants.SendAsync(HttpMethod.Put, "/api/team-variables/DEPLOY_TOKEN", owner, team, body), "set");
        await ShouldSucceedAsync(await _tenants.SendAsync(HttpMethod.Delete, "/api/team-variables/DEPLOY_TOKEN", owner, team), "delete");

        var deleted = await _tenants.DbAsync(db => db.Variable.AsNoTracking().SingleAsync(v => v.TeamId == team && v.Name == "DEPLOY_TOKEN" && v.DeletedDate != null));
        deleted.ValueEncrypted.ShouldBeNull("the deleted secret's ciphertext must not outlive the delete — check VariableService.DeleteAsync if this fails");
        deleted.ValueType.ShouldBe(VariableValueType.Secret, "the row itself stays as the audit trail");

        await ShouldSucceedAsync(await _tenants.SendAsync(HttpMethod.Put, "/api/team-variables/DEPLOY_TOKEN", owner, team, body), "set again under the freed name");
        (await ShouldSucceedAsync(await _tenants.SendAsync(HttpMethod.Get, "/api/team-variables", owner, team), "list")).ShouldContain("DEPLOY_TOKEN");
    }

    private static async Task<string> ShouldSucceedAsync(HttpResponseMessage response, string step)
    {
        var text = await response.Content.ReadAsStringAsync();

        response.IsSuccessStatusCode.ShouldBeTrue($"{step}: got {(int)response.StatusCode}: {text}");

        return text;
    }
}
