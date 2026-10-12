using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.IntegrationTests.Binding;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Auth;

/// <summary>
/// A person's own provider token, lent to a team while they belong to it, through the whole HTTP surface — and what is
/// left of it once they leave or are removed.
///
/// <para>Tier: 🟢 High-fidelity (Rule 12) — the real ASP.NET pipeline (routing, JWT auth, the <c>X-Team-Id</c> scope,
/// model binding, the controllers, the exception filter, the mediator and its authorization behaviors), the real
/// workflow engine drained in-process, and real Postgres. One documented seam: the provider is the Git test provider,
/// which answers whoami, lists three fixed repositories, and records every PR open in
/// <see cref="TestPullRequestOpenCapture"/> — so "the write never reached the provider" is observed, not inferred —
/// with a loopback OAuth client for its authorize URL. Two teams per test: the departing person is a Member of one and
/// the Owner of their own.</para>
///
/// <para>Must hold, for both ways out (removed, left): the token stops being the team's — not listed, not browsable,
/// not bindable, not usable to act as them, not even by a run launched before they went — while the token they lent
/// their other team keeps working; the same for a token added without naming an owner, and for a departure from before
/// departures retired tokens once the departure sweep has run; an owner id outside the caller's reach is refused on both
/// connect paths, with one answer for "elsewhere" and "nowhere"; and unlinking an identity breaks, visibly, the
/// repositories bound through its token. And an instance Admin, who stands in every team without a membership row, lends a
/// team a token that keeps working — sweep included — until the role is taken away.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class DepartedMemberCredentialE2ETests : IClassFixture<DepartedMemberCredentialE2ETests.GitProviderApiFactory>
{
    private const int MaxSweepPasses = 10;

    /// <summary>The departure sweep is deployment-wide: every earlier test's leftover token of someone without standing is a candidate ahead of ours, fifty a pass.</summary>
    private const int MaxDrainPasses = 50;

    private readonly GitProviderApiFactory _factory;

    public DepartedMemberCredentialE2ETests(GitProviderApiFactory factory) { _factory = factory; }

    public enum Departure { Removed, Left }

    [Theory]
    [InlineData(Departure.Removed)]
    [InlineData(Departure.Left)]
    public async Task A_departed_members_token_stops_working_for_the_team_they_left(Departure departure)
    {
        var world = await SeedWorldAsync();
        var identityA = await LinkTokenAsync(world.DepartingId, world.TeamA, world.InstanceA, "departing-token-a");
        var identityB = await LinkTokenAsync(world.DepartingId, world.TeamB, world.InstanceB, "departing-token-b");
        var tokenA = await IdentityCredentialAsync(identityA);
        var title = $"opened as a departed member {Guid.NewGuid():N}";

        await ShouldSucceedAsync(await BindAsync(world.OwnerId, world.TeamA, world.InstanceA, tokenA, "acme/web"), "fixture check: while they are in the team, an Owner can bind through their token");
        await _factory.JobClient.DrainAsync();
        (await ShouldSucceedAsync(await SendAsync(HttpMethod.Get, "/api/credentials", world.MemberId, world.TeamA), "fixture check: list")).ShouldContain(tokenA.ToString());
        (await ShouldSucceedAsync(await SendAsync(HttpMethod.Get, $"/api/repositories/{world.ConnectionRepository}/act-as-candidates", world.MemberId, world.TeamA), "fixture check: picker")).ShouldContain(world.DepartingId.ToString());

        var whileMember = await RunOpenPullRequestAsAsync(world, world.DepartingId, $"{title} while a member");
        whileMember.Status.ShouldBe(WorkflowRunStatus.Success, $"fixture check: while they are in the team, a workflow acts as them — node said: {whileMember.NodeError}");
        _factory.PullRequestOpens.All.Select(o => o.Title).ShouldContain($"{title} while a member", "fixture check: the act-as write reached the provider");

        await DepartAsync(world, departure);

        (await ShouldSucceedAsync(await SendAsync(HttpMethod.Get, "/api/credentials", world.MemberId, world.TeamA), "list")).ShouldNotContain(tokenA.ToString(), Case.Insensitive, $"{departure}: a Member no longer sees the departed member's token");

        var browse = await SendAsync(HttpMethod.Get, $"/api/credentials/{tokenA}/accessible-repositories", world.MemberId, world.TeamA);
        browse.StatusCode.ShouldBeOneOf(new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound }, $"{departure}: a Member can no longer enumerate what the departed member's account reaches");

        var bind = await BindAsync(world.OwnerId, world.TeamA, world.InstanceA, tokenA, "acme/cli");
        ((int)bind.StatusCode).ShouldBeInRange(400, 499, $"{departure}: an Owner can no longer bind through it — {await bind.Content.ReadAsStringAsync()}");

        var run = await RunOpenPullRequestAsAsync(world, world.DepartingId, title);
        ShouldBeRefusedForWantOfAnIdentity(run, $"{departure}: a workflow naming the departed member as its author");
        _factory.PullRequestOpens.All.Select(o => o.Title).ShouldNotContain(title, $"{departure}: the refusal came before any provider call — the departed member's token was never presented");

        await ShouldBeRetiredAsync(tokenA, identityA, departure);

        var theirs = await ShouldSucceedAsync(await SendAsync(HttpMethod.Get, "/api/me/identities", world.DepartingId, world.TeamB), "their identities, from the team they are still in");
        theirs.ShouldContain(identityB.ToString(), Case.Insensitive, $"{departure}: the identity they linked in their own team is untouched");
        theirs.ShouldNotContain(identityA.ToString(), Case.Insensitive, $"{departure}: nothing of theirs is left live on the team they left");
    }

    [Theory]
    [InlineData(Departure.Removed)]
    [InlineData(Departure.Left)]
    public async Task A_run_launched_while_its_author_was_a_member_never_writes_through_their_token_after_they_go(Departure departure)
    {
        // The run is accepted and queued while the person it acts as still belongs to the team; it executes after they
        // have gone. Whoever it acts as is decided when the write happens, not when the run was launched, so the queued
        // act-as node meets the departure and is refused before the provider is asked.
        var world = await SeedWorldAsync();
        await LinkTokenAsync(world.DepartingId, world.TeamA, world.InstanceA, "departing-token-a");
        var title = $"in flight across a departure {Guid.NewGuid():N}";

        var runId = await LaunchOpenPullRequestAsAsync(world, world.DepartingId, title);

        await DepartAsync(world, departure);
        await _factory.JobClient.DrainAsync();

        ShouldBeRefusedForWantOfAnIdentity(await ReadRunAsync(runId), $"{departure}: the run launched before the departure");
        _factory.PullRequestOpens.All.Select(o => o.Title).ShouldNotContain(title, $"{departure}: the in-flight run never presented the departed member's token");
    }

    public enum ConnectPath { Token, OAuth }

    [Theory]
    [InlineData(ConnectPath.Token)]
    [InlineData(ConnectPath.OAuth)]
    public async Task Naming_a_credential_owner_outside_your_reach_is_refused_with_one_answer_for_elsewhere_and_nowhere(ConnectPath path)
    {
        var world = await SeedWorldAsync();
        var elsewhere = await SeedUserInOwnTeamAsync("Victoria Elsewhere");

        var foreign = await StartConnectForAsync(path, world, elsewhere);
        var nowhere = await StartConnectForAsync(path, world, Guid.NewGuid());
        var aboveYou = await StartConnectForAsync(path, world, world.OwnerId);
        var belowYou = await StartConnectForAsync(path, world, world.MemberId);

        foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{path}: a user of another tenant is not someone this team can name");
        nowhere.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{path}: an invented id is refused before the foreign key can turn it into a 500");
        (await nowhere.Content.ReadAsStringAsync()).ShouldBe(await foreign.Content.ReadAsStringAsync(), $"{path}: elsewhere and nowhere must read the same — otherwise the endpoint tells a real account from an invented one");
        aboveYou.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{path}: an Admin does not act on the Owner, so cannot connect a credential in the Owner's name");
        await ShouldSucceedAsync(belowYou, $"control: an Admin may start a {path} connect for a Member below them");

        var listed = await ShouldSucceedAsync(await SendAsync(HttpMethod.Get, "/api/credentials", world.AdminId, world.TeamA), "list");
        listed.ShouldNotContain(elsewhere.ToString(), Case.Insensitive, $"{path}: no credential carries the foreign account's id");
        listed.ShouldNotContain("Victoria Elsewhere", Case.Insensitive, $"{path}: nor discloses the foreign account's display name");
        (await PendingConnectsForAsync(elsewhere)).ShouldBe(0, $"{path}: no connect waits for a callback in the foreign account's name");
    }

    [Fact]
    public async Task Unlinking_an_identity_breaks_the_repositories_bound_through_its_token()
    {
        var world = await SeedWorldAsync();
        var identity = await LinkTokenAsync(world.DepartingId, world.TeamA, world.InstanceA, "token-to-unlink");
        var token = await IdentityCredentialAsync(identity);
        var bound = JsonDocument.Parse(await ShouldSucceedAsync(await BindAsync(world.OwnerId, world.TeamA, world.InstanceA, token, "acme/web"), "bind through the token")).RootElement.GetProperty("id").GetGuid();
        await _factory.JobClient.DrainAsync();

        var unlink = await SendAsync(HttpMethod.Delete, $"/api/me/identities/{identity}", world.DepartingId, world.TeamA);
        unlink.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var repo = await LoadRepositoryAsync(bound);
        repo.Status.ShouldBe(RepositoryStatus.Error, "a repository bound through an unlinked token says it needs a new credential instead of failing at its next run");
        repo.LastError.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_token_added_without_naming_an_owner_goes_when_whoever_added_it_leaves()
    {
        var world = await SeedWorldAsync();

        await ShouldSucceedAsync(await SendAsync(HttpMethod.Patch, $"/api/teams/members/{world.DepartingId}", world.OwnerId, world.TeamA, new { role = "Admin" }), "fixture: the Owner makes them an Admin, who may add tokens");

        var added = await ShouldSucceedAsync(await SendAsync(HttpMethod.Post, "/api/credentials/pat", world.DepartingId, world.TeamA, new { providerInstanceId = world.InstanceA, displayName = "my token", token = "glpat-mine" }), "add a token without naming an owner");
        var tokenId = JsonDocument.Parse(added).RootElement.GetProperty("id").GetGuid();

        var listed = await ShouldSucceedAsync(await SendAsync(HttpMethod.Get, "/api/credentials", world.MemberId, world.TeamA), "fixture check: list");
        listed.ShouldContain(tokenId.ToString(), Case.Insensitive, "fixture check: while they are in the team, the token is one of its credentials");
        listed.ShouldContain(world.DepartingId.ToString(), Case.Insensitive, "the token is listed as theirs — a personal token added without naming an owner is the caller's own");

        await DepartAsync(world, Departure.Left);

        (await ShouldSucceedAsync(await SendAsync(HttpMethod.Get, "/api/credentials", world.MemberId, world.TeamA), "list")).ShouldNotContain(tokenId.ToString(), Case.Insensitive, "a Member no longer sees the token of the person who added it and left");

        var bind = await BindAsync(world.OwnerId, world.TeamA, world.InstanceA, tokenId, "acme/web");
        ((int)bind.StatusCode).ShouldBeInRange(400, 499, $"an Owner can no longer bind through it — {await bind.Content.ReadAsStringAsync()}");

        var token = await LoadCredentialAsync(tokenId);
        token.Status.ShouldBe(CredentialStatus.Revoked);
        token.EncryptedPayload.ShouldBe(string.Empty);
        token.DeletedDate.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_member_who_left_before_departures_retired_tokens_loses_them_once_the_sweep_runs()
    {
        // The state every departure before this change left behind, and every departure an older pod serves during the
        // rolling deploy: the membership row is gone and the token is still the team's.
        var world = await SeedWorldAsync();
        var identityA = await LinkTokenAsync(world.DepartingId, world.TeamA, world.InstanceA, "departing-token-a");
        var identityB = await LinkTokenAsync(world.DepartingId, world.TeamB, world.InstanceB, "departing-token-b");
        var tokenA = await IdentityCredentialAsync(identityA);
        var tokenB = await IdentityCredentialAsync(identityB);

        await ShouldSucceedAsync(await BindAsync(world.OwnerId, world.TeamA, world.InstanceA, tokenA, "acme/web"), "fixture: an Owner binds through their token while they are in the team");
        await _factory.JobClient.DrainAsync();

        await DeleteMembershipAsDeparturesUsedToAsync(world.TeamA, world.DepartingId);

        (await ShouldSucceedAsync(await SendAsync(HttpMethod.Get, "/api/credentials", world.MemberId, world.TeamA), "fixture check: list")).ShouldContain(tokenA.ToString(), Case.Insensitive, "fixture check: a departure from before left the token listed");

        await SweepDepartedUntilRetiredAsync(tokenA);

        (await ShouldSucceedAsync(await SendAsync(HttpMethod.Get, "/api/credentials", world.MemberId, world.TeamA), "list")).ShouldNotContain(tokenA.ToString(), Case.Insensitive, "a Member no longer sees the departed member's token");

        var browse = await SendAsync(HttpMethod.Get, $"/api/credentials/{tokenA}/accessible-repositories", world.MemberId, world.TeamA);
        browse.StatusCode.ShouldBeOneOf(new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound }, "a Member can no longer enumerate what the departed member's account reaches");

        var bind = await BindAsync(world.OwnerId, world.TeamA, world.InstanceA, tokenA, "acme/cli");
        ((int)bind.StatusCode).ShouldBeInRange(400, 499, $"an Owner can no longer bind through it — {await bind.Content.ReadAsStringAsync()}");

        (await LoadRepositoryAsync(await RepositoryBoundAsync(tokenA, "acme/web"))).Status.ShouldBe(RepositoryStatus.Error, "the repository bound through it says it needs a new credential");

        (await ShouldSucceedAsync(await SendAsync(HttpMethod.Get, "/api/credentials", world.DepartingId, world.TeamB), "their own team's list")).ShouldContain(tokenB.ToString(), Case.Insensitive, "the token they lent the team they are still in is untouched");
        (await LoadCredentialAsync(tokenB)).Status.ShouldBe(CredentialStatus.Active);
    }

    [Fact]
    public async Task An_instance_admin_outside_the_team_lends_it_a_token_that_stays_until_they_lose_the_role()
    {
        // A live instance Admin stands in every team as an Owner does — the pipeline lets them in without a membership row
        // — and lends a team a token without joining it. That must keep working: connecting, binding through the token, a
        // workflow acting as them, and the departure sweep leaving it alone on deploy. Once the role is taken away (by an
        // operator: nothing in the app writes role_user) they stand nowhere, and the sweep retires what they lent.
        var world = await SeedWorldAsync();
        var adminId = await SeedInstanceAdminAsync();
        var identity = await LinkTokenAsync(adminId, world.TeamA, world.InstanceA, "admins-token");
        var token = await IdentityCredentialAsync(identity);
        var title = $"opened as an instance admin {Guid.NewGuid():N}";

        var added = await ShouldSucceedAsync(await SendAsync(HttpMethod.Post, "/api/credentials/pat", adminId, world.TeamA, new { providerInstanceId = world.InstanceA, displayName = "admin's token", token = "glpat-admin" }), "the instance Admin adds a token without naming an owner");
        var addedId = JsonDocument.Parse(added).RootElement.GetProperty("id").GetGuid();
        await ShouldSucceedAsync(await SendAsync(HttpMethod.Post, "/api/credentials/oauth/init", adminId, world.TeamA, new { providerInstanceId = world.InstanceA, displayName = "admin's connect" }), "the instance Admin starts an OAuth connect of their own");
        (await PendingConnectsForAsync(adminId)).ShouldBe(1, "the connect waits for its callback in the instance Admin's name");

        await ShouldSucceedAsync(await BindAsync(world.OwnerId, world.TeamA, world.InstanceA, token, "acme/web"), "the Owner binds through the instance Admin's token");
        await _factory.JobClient.DrainAsync();
        (await ShouldSucceedAsync(await SendAsync(HttpMethod.Get, $"/api/repositories/{world.ConnectionRepository}/act-as-candidates", world.MemberId, world.TeamA), "picker")).ShouldContain(adminId.ToString(), Case.Insensitive, "the picker offers the instance Admin");

        var control = await IdentityCredentialAsync(await LinkTokenAsync(world.DepartingId, world.TeamA, world.InstanceA, "departing-token-a"));
        await DeleteMembershipAsDeparturesUsedToAsync(world.TeamA, world.DepartingId);
        await SweepDepartedUntilRetiredAsync(control);
        await DrainDepartureSweepAsync();

        foreach (var keptId in new[] { token, addedId })
            (await LoadCredentialAsync(keptId)).Status.ShouldBe(CredentialStatus.Active, "the sweep leaves what an instance Admin lent a team alone — they stand in it");

        (await LoadRepositoryAsync(await RepositoryBoundAsync(token, "acme/web"))).Status.ShouldBe(RepositoryStatus.Active, "the repository bound through the Admin's token keeps working across the sweep");

        var asAdmin = await RunOpenPullRequestAsAsync(world, adminId, title);
        asAdmin.Status.ShouldBe(WorkflowRunStatus.Success, $"a Member's workflow acts as the instance Admin — node said: {asAdmin.NodeError}");
        _factory.PullRequestOpens.All.Select(o => o.Title).ShouldContain(title, "the act-as write reached the provider through the Admin's token");

        await RevokeInstanceAdminAsync(adminId);
        await SweepDepartedUntilRetiredAsync(token);
        await SweepDepartedUntilRetiredAsync(addedId);

        (await LoadRepositoryAsync(await RepositoryBoundAsync(token, "acme/web"))).Status.ShouldBe(RepositoryStatus.Error, "once the role is gone, the repository bound through their token says it needs a new credential");
        (await ShouldSucceedAsync(await SendAsync(HttpMethod.Get, "/api/credentials", world.MemberId, world.TeamA), "list")).ShouldNotContain(token.ToString(), Case.Insensitive, "a Member no longer sees the ex-Admin's token");
        (await ShouldSucceedAsync(await SendAsync(HttpMethod.Get, $"/api/repositories/{world.ConnectionRepository}/act-as-candidates", world.MemberId, world.TeamA), "picker")).ShouldNotContain(adminId.ToString(), Case.Insensitive, "the picker no longer offers them");

        var afterDemotion = await RunOpenPullRequestAsAsync(world, adminId, $"{title} after the role went");
        ShouldBeRefusedForWantOfAnIdentity(afterDemotion, "a workflow naming the ex-Admin as its author");
    }

    // ── Flow steps ────────────────────────────────────────────────────────────────

    /// <summary>What departures did before they retired anything: the membership row goes, and nothing else does.</summary>
    private async Task DeleteMembershipAsDeparturesUsedToAsync(Guid teamId, Guid userId)
    {
        using var scope = _factory.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().TeamMembership.Where(m => m.TeamId == teamId && m.UserId == userId).ExecuteDeleteAsync();
    }

    /// <summary>
    /// The departure sweep's recurring command until a pass retires nothing, so every candidate it has has been retired —
    /// for asserting a token is NOT one.
    /// </summary>
    private async Task DrainDepartureSweepAsync()
    {
        for (var pass = 1; pass <= MaxDrainPasses; pass++)
        {
            using var scope = _factory.Services.CreateScope();

            if (await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new RetireDepartedMembersCredentialsCommand()) == 0) return;
        }

        throw new ShouldAssertException($"RetireDepartedMembersCredentialsCommand still retired tokens after {MaxDrainPasses} passes — check that a retired token stops being a candidate.");
    }

    /// <summary>An account that signs in, holding the instance Admin role through a <c>role_user</c> row, and no membership anywhere.</summary>
    private async Task<Guid> SeedInstanceAdminAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var admin = NewUser("instance-admin", Guid.NewGuid().ToString("N")[..8]);

        db.User.Add(admin);
        db.RoleUser.Add(new RoleUser { Id = Guid.NewGuid(), RoleId = await db.Role.Where(r => r.Name == Roles.Admin).Select(r => r.Id).SingleAsync(), UserId = admin.Id });
        await db.SaveChangesAsync();

        return admin.Id;
    }

    /// <summary>How the role is taken away: nothing in the app writes <c>role_user</c>, so an operator deletes the row.</summary>
    private async Task RevokeInstanceAdminAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().RoleUser.Where(r => r.UserId == userId).ExecuteDeleteAsync();
    }

    /// <summary>The departure sweep's recurring command through the app's own mediator, until the token is retired.</summary>
    private async Task SweepDepartedUntilRetiredAsync(Guid credentialId)
    {
        for (var pass = 1; pass <= MaxSweepPasses; pass++)
        {
            using (var scope = _factory.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new RetireDepartedMembersCredentialsCommand());

            if ((await LoadCredentialAsync(credentialId)).DeletedDate != null) return;
        }

        throw new ShouldAssertException($"RetireDepartedMembersCredentialsCommand had not retired credential {credentialId} after {MaxSweepPasses} passes — check that its candidate query finds a live Personal token whose owner holds no membership in its team.");
    }

    private async Task<Guid> LinkTokenAsync(Guid userId, Guid teamId, Guid instanceId, string token)
    {
        var body = await ShouldSucceedAsync(await SendAsync(HttpMethod.Post, "/api/me/identities/pat", userId, teamId, new { providerInstanceId = instanceId, accessToken = token }), "link a token");

        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> BindAsync(Guid userId, Guid teamId, Guid instanceId, Guid credentialId, string project) =>
        SendAsync(HttpMethod.Post, "/api/repositories/bind", userId, teamId, new { providerInstanceId = instanceId, credentialId, projectIdentifier = project });

    /// <summary>The Admin starts a connect in <paramref name="ownerUserId"/>'s name: a token added outright, or an OAuth connect begun and left waiting for its callback.</summary>
    private Task<HttpResponseMessage> StartConnectForAsync(ConnectPath path, World world, Guid ownerUserId) => path == ConnectPath.Token
        ? SendAsync(HttpMethod.Post, "/api/credentials/pat", world.AdminId, world.TeamA, new { providerInstanceId = world.InstanceA, ownerUserId, displayName = "named token", token = "glpat-named" })
        : SendAsync(HttpMethod.Post, "/api/credentials/oauth/init", world.AdminId, world.TeamA, new { providerInstanceId = world.InstanceA, displayName = "named connect", intendedOwnerUserId = ownerUserId });

    private async Task DepartAsync(World world, Departure departure)
    {
        var departed = departure == Departure.Removed
            ? await SendAsync(HttpMethod.Delete, $"/api/teams/members/{world.DepartingId}", world.OwnerId, world.TeamA)
            : await SendAsync(HttpMethod.Post, "/api/teams/members/leave", world.DepartingId, world.TeamA);

        departed.StatusCode.ShouldBe(HttpStatusCode.NoContent, $"{departure}: {await departed.Content.ReadAsStringAsync()}");
    }

    /// <summary>A Member authors a one-node workflow whose <c>git.open_pr</c> acts as <paramref name="actAsUserId"/>, runs it, and drains the engine.</summary>
    private async Task<RunOutcome> RunOpenPullRequestAsAsync(World world, Guid actAsUserId, string title)
    {
        var runId = await LaunchOpenPullRequestAsAsync(world, actAsUserId, title);

        await _factory.JobClient.DrainAsync();

        return await ReadRunAsync(runId);
    }

    /// <summary>A Member authors and launches the workflow; the run is accepted and queued, and nothing executes until the engine is drained.</summary>
    private async Task<Guid> LaunchOpenPullRequestAsAsync(World world, Guid actAsUserId, string title)
    {
        var definition = new
        {
            name = $"act-as-{Guid.NewGuid():N}"[..24],
            definition = new
            {
                schemaVersion = 1,
                nodes = new object[]
                {
                    new { id = "start", typeKey = "trigger.manual", config = new { }, inputs = new { } },
                    new { id = "act", typeKey = "git.open_pr", config = new { }, inputs = new Dictionary<string, object> { ["repositoryId"] = world.ConnectionRepository.ToString(), ["title"] = title, ["sourceBranch"] = "feature", ["targetBranch"] = "main", ["actAsUserId"] = actAsUserId.ToString() } },
                    new { id = "end", typeKey = "builtin.terminal", config = new { }, inputs = new { } },
                },
                edges = new object[] { new { from = "start", to = "act" }, new { from = "act", to = "end" } },
            },
            activations = Array.Empty<object>(),
            enabled = true,
        };

        var created = await ShouldSucceedAsync(await SendAsync(HttpMethod.Post, "/api/workflows", world.MemberId, world.TeamA, definition), "a Member authors the workflow");
        var workflowId = JsonDocument.Parse(created).RootElement.GetProperty("id").GetGuid();

        var launched = await ShouldSucceedAsync(await SendAsync(HttpMethod.Post, $"/api/workflows/{workflowId}/run", world.MemberId, world.TeamA, new { }), "a Member runs it");

        return JsonDocument.Parse(launched).RootElement.GetProperty("runId").GetGuid();
    }

    private async Task<RunOutcome> ReadRunAsync(Guid runId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var status = await db.WorkflowRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.Status).SingleAsync();
        var nodeError = await db.WorkflowRunNode.AsNoTracking().Where(n => n.RunId == runId && n.NodeId == "act").Select(n => n.Error).SingleOrDefaultAsync();

        return new RunOutcome(status, nodeError);
    }

    private sealed record RunOutcome(WorkflowRunStatus Status, string? NodeError);

    /// <summary>Failed, and failed because nobody may be acted as — not for any other reason a run can fail.</summary>
    private static void ShouldBeRefusedForWantOfAnIdentity(RunOutcome run, string what)
    {
        run.Status.ShouldBe(WorkflowRunStatus.Failure, $"{what} is refused — node said: {run.NodeError}");
        run.NodeError.ShouldNotBeNull();
        run.NodeError.ShouldContain("haven't linked one", Case.Insensitive, $"{what} fails for want of an identity to act as, not for some other reason");
    }

    private async Task ShouldBeRetiredAsync(Guid credentialId, Guid identityId, Departure departure)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var credential = await db.Credential.AsNoTracking().SingleAsync(c => c.Id == credentialId);
        var identity = await db.UserProviderIdentity.AsNoTracking().SingleAsync(i => i.Id == identityId);
        var boundRepo = await db.Repository.AsNoTracking().SingleAsync(r => r.CredentialId == credentialId && r.FullPath == "acme/web");

        credential.Status.ShouldBe(CredentialStatus.Revoked, $"{departure}: the token is revoked");
        credential.EncryptedPayload.ShouldBe(string.Empty, $"{departure}: nothing usable is left at rest");
        credential.DeletedDate.ShouldNotBeNull();
        identity.DeletedDate.ShouldNotBeNull($"{departure}: the act-as identity goes with its token");
        boundRepo.Status.ShouldBe(RepositoryStatus.Error, $"{departure}: the repository bound through the token says it needs a new credential");
    }

    private async Task<int> PendingConnectsForAsync(Guid ownerUserId)
    {
        using var scope = _factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().OAuthPendingState.AsNoTracking().CountAsync(s => s.IntendedOwnerUserId == ownerUserId);
    }

    private async Task<Repository> LoadRepositoryAsync(Guid repositoryId)
    {
        using var scope = _factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().Repository.AsNoTracking().SingleAsync(r => r.Id == repositoryId);
    }

    private async Task<Credential> LoadCredentialAsync(Guid credentialId)
    {
        using var scope = _factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().Credential.AsNoTracking().SingleAsync(c => c.Id == credentialId);
    }

    private async Task<Guid> RepositoryBoundAsync(Guid credentialId, string fullPath)
    {
        using var scope = _factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().Repository.AsNoTracking().Where(r => r.CredentialId == credentialId && r.FullPath == fullPath).Select(r => r.Id).SingleAsync();
    }

    private async Task<Guid> IdentityCredentialAsync(Guid identityId)
    {
        using var scope = _factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().UserProviderIdentity.AsNoTracking().Where(i => i.Id == identityId).Select(i => i.CredentialId).SingleAsync();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Guid userId, Guid teamId, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body != null) request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(userId, TestToken.SeedStamp));
        request.Headers.Add("X-Team-Id", teamId.ToString());

        return await _factory.CreateClient().SendAsync(request);
    }

    private static async Task<string> ShouldSucceedAsync(HttpResponseMessage response, string what)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue($"{what}: {(int)response.StatusCode} {body}");

        return body;
    }

    // ── World ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Team A: an Owner, an Admin, a Member and the departing person (a Member), one Git instance, and a repository bound
    /// through a team-service token. Team B: the departing person's own, with its own Git instance. GUID-suffixed throughout.
    /// </summary>
    private sealed record World(Guid TeamA, Guid TeamB, Guid OwnerId, Guid AdminId, Guid MemberId, Guid DepartingId, Guid InstanceA, Guid InstanceB, Guid ConnectionRepository);

    private async Task<World> SeedWorldAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var encryptor = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>();
        var serializer = scope.ServiceProvider.GetRequiredService<ICredentialPayloadSerializer>();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var owner = NewUser("owner", suffix);
        var admin = NewUser("admin", suffix);
        var member = NewUser("member", suffix);
        var departing = NewUser("departing", suffix);
        var teamA = NewTeam("a", suffix);
        var teamB = NewTeam("b", suffix);
        var instanceA = NewInstance(teamA, suffix + "a", encryptor);
        var instanceB = NewInstance(teamB, suffix + "b", encryptor);
        var connection = new Credential { Id = Guid.NewGuid(), TeamId = teamA.Id, ProviderInstanceId = instanceA.Id, Ownership = CredentialOwnership.TeamService, AuthType = AuthType.Pat, DisplayName = "team connection", EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "team-token" })), Status = CredentialStatus.Active };
        var repo = new Repository { Id = Guid.NewGuid(), TeamId = teamA.Id, ProviderInstanceId = instanceA.Id, CredentialId = connection.Id, ExternalId = $"ext-{suffix}", NamespacePath = "acme", Name = "api", FullPath = "acme/api", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "https://git.test/acme/api", Status = RepositoryStatus.Active };

        db.User.AddRange(owner, admin, member, departing);
        db.Team.AddRange(teamA, teamB);
        db.TeamMembership.AddRange(Membership(teamA, owner, TeamRole.Owner), Membership(teamA, admin, TeamRole.Admin), Membership(teamA, member, TeamRole.Member), Membership(teamA, departing, TeamRole.Member), Membership(teamB, departing, TeamRole.Owner));
        db.ProviderInstance.AddRange(instanceA, instanceB);
        db.Credential.Add(connection);
        db.Repository.Add(repo);
        await db.SaveChangesAsync();

        return new World(teamA.Id, teamB.Id, owner.Id, admin.Id, member.Id, departing.Id, instanceA.Id, instanceB.Id, repo.Id);
    }

    private async Task<Guid> SeedUserInOwnTeamAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = NewUser("elsewhere", suffix);
        var team = NewTeam("elsewhere", suffix);

        user.Name = name;
        db.User.Add(user);
        db.Team.Add(team);
        db.TeamMembership.Add(Membership(team, user, TeamRole.Owner));
        await db.SaveChangesAsync();

        return user.Id;
    }

    private static User NewUser(string label, string suffix) => new() { Id = Guid.NewGuid(), Email = $"{label}-{suffix}@departed.test", Name = $"{label}-{suffix}", SecurityStamp = TestToken.SeedStamp, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };

    private static Team NewTeam(string label, string suffix) => new() { Id = Guid.NewGuid(), Slug = $"{label}-{suffix}", Name = label, Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };

    private static TeamMembership Membership(Team team, User user, TeamRole role) => new() { Id = Guid.NewGuid(), TeamId = team.Id, UserId = user.Id, Role = role, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };

    private static ProviderInstance NewInstance(Team team, string suffix, IPayloadEncryptor encryptor) =>
        new() { Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.Git, DisplayName = "Git", BaseUrl = $"https://git-{suffix}.test", OauthClientId = "client", OauthClientSecretEnc = encryptor.Encrypt("secret") };

    /// <summary>
    /// The app with one seam: the Git test provider stands in for GitHub/GitLab, recording every PR open, and a loopback
    /// OAuth client stands in for its authorize endpoint.
    /// </summary>
    public sealed class GitProviderApiFactory : TaskLaunchApiFactory
    {
        public TestPullRequestOpenCapture PullRequestOpens => Services.GetAutofacRoot().Resolve<TestPullRequestOpenCapture>();

        protected override void ConfigureContractTestServices(ContainerBuilder builder)
        {
            builder.RegisterType<TestRemoteHookStore>().AsSelf().SingleInstance();
            builder.RegisterType<TestPullRequestOpenCapture>().AsSelf().SingleInstance();
            builder.RegisterType<TestRepositoryProvider>().AsImplementedInterfaces().InstancePerLifetimeScope();
            builder.RegisterType<LoopbackOAuthClient>().As<IOAuthClient>().SingleInstance();
        }
    }

    /// <summary>The Git provider's OAuth endpoints on loopback. Only the authorize URL is reached — no test here completes a connect, so the token endpoints refuse.</summary>
    private sealed class LoopbackOAuthClient : IOAuthClient
    {
        public ProviderKind Kind => ProviderKind.Git;

        public Uri BuildAuthorizeUrl(OAuthAuthorizeInput input) => new($"http://127.0.0.1/oauth/authorize?state={input.State}");

        public Task<OAuthTokenResponse> ExchangeCodeAsync(OAuthCodeExchangeInput input, CancellationToken cancellationToken) => throw new NotSupportedException("no test in this suite completes an OAuth connect");

        public Task<OAuthTokenResponse> RefreshAsync(OAuthRefreshInput input, CancellationToken cancellationToken) => throw new NotSupportedException("no test in this suite refreshes an OAuth token");

        public Task RevokeAsync(OAuthRevokeInput input, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
