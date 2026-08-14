using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Webhooks;

/// <summary>
/// The reveal endpoint on the WIRE — routing, JWT auth, the X-Team-Id scope behavior, the team
/// permission behavior, and the exception filter that turns a refusal into a status a client can
/// act on.
///
/// <para>This endpoint hands out the value every inbound delivery is authenticated against, so
/// "who is refused" is the whole contract, and refusal is decided by a pipeline behavior whose
/// verdict only becomes an HTTP status inside the real filter. The mediator-direct suite sees a
/// <c>TenantAccessDeniedException</c> and stops there; whether the client receives a 403 or an
/// unshaped 500 is decided at this tier and nowhere else.</para>
///
/// <para>The pair matters as much as the gate: a Member must still be able to OPEN the tab. A gate
/// that also closed the read would have made the diagnosis an admin-only privilege, which is the
/// opposite of the point.</para>
///
/// <para>Tier: 🟢 High-fidelity — real app host, real Postgres, real pipeline.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class RepositoryWebhookSecretEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private const string StagedSecret = "whsec-wire-0b73ae";

    private readonly TaskLaunchApiFactory _factory;

    public RepositoryWebhookSecretEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task An_admin_receives_the_secret()
    {
        var seed = await SeedAsync();

        var response = await SendAsync(HttpMethod.Post, SecretUrl(seed), seed.Admin, seed.TeamId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(response, "200 with the plaintext secret"));

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("webhookId").GetGuid().ShouldBe(seed.WebhookId);
        body.GetProperty("secret").GetString().ShouldBe(StagedSecret, customMessage: "the operator re-enters this at the provider by hand — a masked value would be useless");
    }

    [Fact]
    public async Task A_member_is_refused()
    {
        var seed = await SeedAsync();

        var response = await SendAsync(HttpMethod.Post, SecretUrl(seed), seed.Member, seed.TeamId);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await DescribeAsync(response, "403 — repos.manage is an Admin-tier capability"));

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("code").GetString().ShouldBe(FailureCodes.Forbidden, customMessage: "the SPA branches on the code, not on the prose");
        (await response.Content.ReadAsStringAsync()).ShouldNotContain(StagedSecret, customMessage: "a refusal that still writes the secret into the body has refused nothing");
    }

    [Fact]
    public async Task A_member_can_still_open_the_tab()
    {
        var seed = await SeedAsync();

        var response = await SendAsync(HttpMethod.Get, $"/api/repositories/{seed.RepositoryId}/webhooks", seed.Member, seed.TeamId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(response, "200 — the diagnosis is not an admin privilege"));

        var payload = await response.Content.ReadAsStringAsync();
        payload.ShouldContain(seed.WebhookId.ToString(), customMessage: "the tab's read must actually carry the hook");
        payload.ShouldNotContain(StagedSecret, customMessage: "the tab read put the signing secret on the wire — the split into two endpoints exists precisely to stop that");
    }

    [Fact]
    public async Task Another_teams_member_is_refused()
    {
        var seed = await SeedAsync();
        var outsider = await SeedAsync();

        // An Admin of their OWN team, naming someone else's repository by id.
        var response = await SendAsync(HttpMethod.Post, SecretUrl(seed), outsider.Admin, outsider.TeamId);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await DescribeAsync(response, "403 — the repository resolves to a team the caller has no standing on"));
        (await response.Content.ReadAsStringAsync()).ShouldNotContain(StagedSecret);
    }

    [Fact]
    public async Task Without_a_team_header_it_is_refused()
    {
        var seed = await SeedAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, SecretUrl(seed));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(seed.Admin, TestToken.SeedStamp));

        var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await DescribeAsync(response, "403 — the team is never inferred from the repository, so no header is a refusal"));
    }

    /// <summary>
    /// The refused-deliveries read, on the wire. It carries the provider's own headers and the verifier's
    /// diagnostic, so who may see it is a contract and not an implementation detail — and, like the tab
    /// read above, a Member must be able to: the refusals ARE the diagnosis.
    /// </summary>
    [Fact]
    public async Task A_member_can_read_the_refusals()
    {
        var seed = await SeedAsync();

        var response = await SendAsync(HttpMethod.Get, RefusalsUrl(seed), seed.Member, seed.TeamId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(response, "200 — a refusal is what the operator came to read"));

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.TryGetProperty("deliveries", out _).ShouldBeTrue(customMessage: "the shape the tab consumes");
        body.TryGetProperty("cap", out _).ShouldBeTrue(customMessage: "the page says the cap out loud rather than inventing one");
    }

    [Fact]
    public async Task Another_teams_refusals_are_refused()
    {
        var seed = await SeedAsync();
        var outsider = await SeedAsync();

        var response = await SendAsync(HttpMethod.Get, RefusalsUrl(seed), outsider.Admin, outsider.TeamId);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await DescribeAsync(response, "403 — refused deliveries carry the provider's headers for someone else's repository"));
    }

    [Fact]
    public async Task Reading_refusals_without_a_session_is_unauthorized()
    {
        var seed = await SeedAsync();

        var response = await _factory.CreateClient().GetAsync(RefusalsUrl(seed));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await DescribeAsync(response, "401 — behind the global fallback policy, unlike the receiver"));
    }

    [Fact]
    public async Task Without_a_session_it_is_unauthorized()
    {
        var seed = await SeedAsync();

        var response = await _factory.CreateClient().PostAsync(SecretUrl(seed), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await DescribeAsync(response, "401 — this endpoint is behind the global fallback policy, unlike the receiver"));
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static string RefusalsUrl(SeededWebhook seed) => $"/api/repositories/{seed.RepositoryId}/rejected-deliveries";

    private static string SecretUrl(SeededWebhook seed) => $"/api/repositories/{seed.RepositoryId}/webhooks/{seed.WebhookId}/secret";

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Guid userId, Guid teamId)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(userId, TestToken.SeedStamp));
        request.Headers.Add("X-Team-Id", teamId.ToString());

        return await _factory.CreateClient().SendAsync(request);
    }

    private static async Task<string> DescribeAsync(HttpResponseMessage response, string expected) =>
        $"expected {expected} but got {(int)response.StatusCode}; body: {await response.Content.ReadAsStringAsync()}";

    /// <summary>
    /// Seeds straight to the rows. Going through bind would need a live provider and a credential
    /// with webhook scope, which is a different thing to test and would make a failure here
    /// ambiguous between "the gate is wrong" and "the bind is wrong".
    /// </summary>
    private async Task<SeededWebhook> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var admin = new User { Id = Guid.NewGuid(), SecurityStamp = TestToken.SeedStamp, Email = $"adm-{suffix}@test.local", Name = "Admin", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };
        var member = new User { Id = Guid.NewGuid(), SecurityStamp = TestToken.SeedStamp, Email = $"mem-{suffix}@test.local", Name = "Member", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"hook-{suffix}", Name = "Hook Wire", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.Git, DisplayName = "Test", BaseUrl = "https://test.local", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };

        var repositoryId = Guid.NewGuid();
        var webhookId = Guid.NewGuid();

        db.User.AddRange(admin, member);
        db.Team.Add(team);
        db.TeamMembership.AddRange(
            new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = admin.Id, Role = TeamRole.Admin, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId },
            new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = member.Id, Role = TeamRole.Member, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.ProviderInstance.Add(instance);
        db.Repository.Add(new Repository
        {
            Id = repositoryId,
            TeamId = team.Id,
            ProviderInstanceId = instance.Id,
            ExternalId = $"id-wire-{suffix}",
            NamespacePath = "acme",
            Name = $"wire-{suffix}",
            FullPath = $"acme/wire-{suffix}",
            DefaultBranch = "main",
            Visibility = RepositoryVisibility.Private,
            WebUrl = "https://test.local",
            Status = RepositoryStatus.Active,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });
        db.RepositoryWebhook.Add(new RepositoryWebhook
        {
            Id = webhookId,
            RepositoryId = repositoryId,
            CallbackUrl = $"https://test.local/api/webhooks/{webhookId}",
            // Encrypted by the HOST's own encryptor, so the reveal is a real round-trip through
            // the same key-ring production uses rather than a fixture handing back its own string.
            SecretEnc = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>().Encrypt(StagedSecret),
            SubscribedEvents = new List<string> { "push" },
            Active = true,
            RegistrationStatus = RepositoryWebhookRegistrationStatus.Registered,
            ExternalId = "hook-wire",
            NextAttemptAt = DateTimeOffset.UtcNow,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();

        return new SeededWebhook(team.Id, admin.Id, member.Id, repositoryId, webhookId);
    }

    private sealed record SeededWebhook(Guid TeamId, Guid Admin, Guid Member, Guid RepositoryId, Guid WebhookId);
}
