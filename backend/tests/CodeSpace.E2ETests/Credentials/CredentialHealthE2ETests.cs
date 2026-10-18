using System.Net;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Credentials;

/// <summary>
/// 🟢 High fidelity: a person links a GitLab personal access token through the real HTTP surface
/// (<c>POST /api/me/identities/pat</c>: the real mediator pipeline, the real GitLab provider over NGitLab and raw HTTP, the
/// probe that reads the token's own record) against real Postgres and a <see cref="GitLabDoorkeeperStub"/> on a loopback
/// port, and reads what the team's credential list says about it (<c>GET /api/credentials</c>). The one production class
/// driven directly is the recurring probe's command, sent through the host's own mediator — the Hangfire tick that would
/// send it is not what is under test.
///
/// <para>The story: GitLab gives the token an expiry three days out, so the list says it is <c>expiring</c> with the days
/// left, before anything has failed. Then GitLab starts refusing the token; within one sweep the credential is
/// <c>rejected</c> and says why, and the next use of it — here, listing the repositories it can reach — fails with a typed
/// <c>credential_needs_reconnect</c> that names it, instead of GitLab's 401 surfacing as a 422 at the first call a run
/// makes.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Category", "ProviderE2E")]
[Trait("Surface", "Http")]
public sealed class CredentialHealthE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;
    private readonly TenantHarness _tenants;

    public CredentialHealthE2ETests(TaskLaunchApiFactory factory)
    {
        _factory = factory;
        _tenants = new TenantHarness(factory);
    }

    [Fact]
    public async Task A_token_that_expires_soon_reads_expiring_and_once_gitlab_refuses_it_one_sweep_marks_it_and_its_next_use_names_what_to_do()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        gitlab.AddAccessToken("glpat-e2e-expiring", "nacho", DateTimeOffset.UtcNow.AddDays(3));
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);

        var linked = await _tenants.SendAsync(HttpMethod.Post, "/api/me/identities/pat", world.UserId, world.TeamId, new { providerInstanceId = world.InstanceId, accessToken = "glpat-e2e-expiring" }).ConfigureAwait(false);
        linked.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(linked).ConfigureAwait(false));

        var expiring = await ReadSummaryAsync(world, "GitLab · nacho").ConfigureAwait(false);

        expiring.GetProperty("health").GetString().ShouldBe("expiring", "the list says so before anything has failed");
        expiring.GetProperty("daysToExpiry").GetInt32().ShouldBeInRange(2, 3, "GitLab's expires_at is a date: it begins at midnight UTC, between two and three days from now");
        expiring.GetProperty("status").GetString().ShouldBe("Active");
        expiring.GetProperty("lastValidatedDate").ValueKind.ShouldNotBe(JsonValueKind.Null, "the link just validated it");

        // GitLab starts refusing the token. The credential is not due again for hours, so it is made due.
        gitlab.RevokeAccessToken("glpat-e2e-expiring");
        var credentialId = expiring.GetProperty("id").GetGuid();
        await _tenants.DbAsync(async db => await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE credential SET last_validated_date = now() - interval '7 hours' WHERE id = {credentialId}")).ConfigureAwait(false);

        await SweepAsync().ConfigureAwait(false);

        var rejected = await ReadSummaryAsync(world, "GitLab · nacho").ConfigureAwait(false);

        rejected.GetProperty("health").GetString().ShouldBe("rejected");
        rejected.GetProperty("status").GetString().ShouldBe("Error");
        rejected.GetProperty("lastError").GetString().ShouldBe("token rejected by GitLab");

        var use = await _tenants.SendAsync(HttpMethod.Get, $"/api/credentials/{credentialId}/accessible-repositories", world.UserId, world.TeamId).ConfigureAwait(false);
        var body = JsonDocument.Parse(await use.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;

        ((int)use.StatusCode).ShouldBe(428, await TenantHarness.DescribeAsync(use).ConfigureAwait(false));
        body.GetProperty("code").GetString().ShouldBe("credential_needs_reconnect");
        body.GetProperty("credentialId").GetGuid().ShouldBe(credentialId, "the failure names the credential, so a client can link to it");
        body.GetProperty("credentialName").GetString().ShouldBe("GitLab · nacho");
    }

    [Fact]
    public async Task A_token_added_through_the_credentials_api_reads_expiring_at_once_without_waiting_for_a_sweep()
    {
        // The group and project tokens a team shares, and a personal token added for someone, do not come through the identity
        // link: POST /api/credentials/pat. GitLab describes the token as it is added, so the very first list says how long it has.
        using var gitlab = new GitLabDoorkeeperStub();
        gitlab.AddAccessToken("glpat-e2e-added", "nacho", DateTimeOffset.UtcNow.AddDays(3));
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);

        var added = await _tenants.SendAsync(HttpMethod.Post, "/api/credentials/pat", world.UserId, world.TeamId, new { providerInstanceId = world.InstanceId, displayName = "added token", token = "glpat-e2e-added" }).ConfigureAwait(false);
        added.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(added).ConfigureAwait(false));

        var expiring = await ReadSummaryAsync(world, "added token").ConfigureAwait(false);

        expiring.GetProperty("health").GetString().ShouldBe("expiring", "the list says so from the first read, before any sweep has run");
        expiring.GetProperty("daysToExpiry").GetInt32().ShouldBeInRange(2, 3);
        expiring.GetProperty("status").GetString().ShouldBe("Active");
        expiring.GetProperty("lastValidatedDate").ValueKind.ShouldNotBe(JsonValueKind.Null, "adding it validated it");
    }

    [Fact]
    public async Task A_credential_list_keeps_every_field_it_had_and_reads_active_for_a_token_with_nothing_to_say()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        gitlab.AddAccessToken("glpat-e2e-forever", "nacho");
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);

        (await _tenants.SendAsync(HttpMethod.Post, "/api/me/identities/pat", world.UserId, world.TeamId, new { providerInstanceId = world.InstanceId, accessToken = "glpat-e2e-forever" }).ConfigureAwait(false)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var summary = await ReadSummaryAsync(world, "GitLab · nacho").ConfigureAwait(false);

        foreach (var property in new[] { "id", "teamId", "providerInstanceId", "ownerUserId", "ownerUserName", "ownership", "authType", "displayName", "status", "expiresDate", "lastValidatedDate", "lastError", "createdDate" })
            summary.TryGetProperty(property, out _).ShouldBeTrue($"'{property}' was in the list before and must stay");

        summary.GetProperty("health").GetString().ShouldBe("active");
        summary.GetProperty("expiresDate").ValueKind.ShouldBe(JsonValueKind.Null, "a token that never expires has no expiry to show");
        summary.GetProperty("daysToExpiry").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ─── Plumbing ──────────────────────────────────────────────────────────────

    private sealed record World(Guid UserId, Guid TeamId, Guid InstanceId);

    /// <summary>One Owner in one team and a token-only GitLab instance on the stub's loopback address.</summary>
    private async Task<World> SeedWorldAsync(GitLabDoorkeeperStub gitlab)
    {
        var user = await _tenants.AccountAsync().ConfigureAwait(false);
        var team = await _tenants.TeamAsync((user, TeamRole.Owner)).ConfigureAwait(false);
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team, Provider = ProviderKind.GitLab, DisplayName = $"gitlab-{Guid.NewGuid():N}"[..14], BaseUrl = gitlab.BaseUrl, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };

        await _tenants.DbAsync(async db =>
        {
            db.ProviderInstance.Add(instance);
            await db.SaveChangesAsync();

            return 0;
        }).ConfigureAwait(false);

        return new World(user, team, instance.Id);
    }

    private async Task<JsonElement> ReadSummaryAsync(World world, string displayName)
    {
        var response = await _tenants.SendAsync(HttpMethod.Get, "/api/credentials", world.UserId, world.TeamId).ConfigureAwait(false);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(response).ConfigureAwait(false));

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        return json.RootElement.EnumerateArray().Single(c => c.GetProperty("displayName").GetString() == displayName).Clone();
    }

    private async Task SweepAsync()
    {
        using var scope = _factory.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new ProbeCredentialsCommand()).ConfigureAwait(false);
    }
}
