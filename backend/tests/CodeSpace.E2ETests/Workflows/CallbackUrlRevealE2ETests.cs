using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Variables;
using CodeSpace.Core.Services.Workflows.Callbacks;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// A run parked on <c>flow.wait_callback</c> is resumed by an anonymous POST of its token, so the token is the power to
/// resume the run with a body of the caller's choosing. A Viewer used to read it off the run page and do exactly that,
/// bypassing <c>runs.control</c>. This drives the whole path over HTTP: a Viewer reads both run surfaces and finds only the
/// withheld placeholder and is refused the reveal; a Member (who holds <c>runs.control</c>) reveals the URL; an anonymous
/// client resumes the run with it, while a guessed token, the placeholder and the stored digest are 404.
///
/// <para>Tier: High fidelity. The real app host (JWT, X-Team-Id, the permission behavior, controllers, the anonymous
/// callback route and its rate limiter), real Postgres, the real engine drained behind <c>POST /api/workflows/{id}/run</c>.
/// Nothing is stubbed.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class CallbackUrlRevealE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    public CallbackUrlRevealE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task A_viewer_never_sees_the_callback_token_and_the_url_a_member_reveals_resumes_the_run_anonymously()
    {
        var world = await SeedWorldAsync();
        var runId = await LaunchCallbackRunAsync(world);
        await _factory.JobClient.DrainAsync();

        var viewerDetail = await ReadOkAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}", world.ViewerId, world.TeamId);
        var viewerPending = await ReadOkAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}/pending-wait", world.ViewerId, world.TeamId);
        var detailWait = JsonDocument.Parse(viewerDetail).RootElement.GetProperty("pendingWait");
        var pendingWait = JsonDocument.Parse(viewerPending).RootElement.GetProperty("wait");
        var waitId = pendingWait.GetProperty("id").GetGuid();

        detailWait.GetProperty("kind").GetString().ShouldBe(WorkflowWaitKinds.Callback, "precondition: the run is parked on its callback");
        detailWait.GetProperty("token").GetString().ShouldBe(WorkflowWaitKinds.WithheldBearerToken, "GET /runs/{id} must not hand a Viewer the token that resumes the run — a placeholder every SPA build decodes stands in for it");
        pendingWait.GetProperty("token").GetString().ShouldBe(WorkflowWaitKinds.WithheldBearerToken, "GET /runs/{id}/pending-wait must not either");

        var viewerReveal = await SendAsync(HttpMethod.Post, RevealPath(runId, waitId), world.ViewerId, world.TeamId);
        viewerReveal.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "revealing the URL is the power to resume the run, which runs.control gates");

        var revealed = await SendAsync(HttpMethod.Post, RevealPath(runId, waitId), world.MemberId, world.TeamId);
        revealed.StatusCode.ShouldBe(HttpStatusCode.OK, await revealed.Content.ReadAsStringAsync());
        var path = JsonDocument.Parse(await revealed.Content.ReadAsStringAsync()).RootElement.GetProperty("path").GetString()!;
        path.ShouldStartWith("/api/workflows/callbacks/");
        var token = path["/api/workflows/callbacks/".Length..];

        viewerDetail.ShouldNotContain(token, customMessage: "the run detail a Viewer read held the token");
        viewerPending.ShouldNotContain(token, customMessage: "the pending wait a Viewer read held the token");
        var (_, digest, _) = await ReadRowAsADatabaseReaderAsync(waitId);
        digest.ShouldBe(WorkflowCallbackTokenSealer.Digest(token));

        (await PostAnonymouslyAsync($"/api/workflows/callbacks/{Guid.NewGuid():N}", "{}")).StatusCode.ShouldBe(HttpStatusCode.NotFound, "a guessed token resumes nothing");
        (await PostAnonymouslyAsync($"/api/workflows/callbacks/{digest}", "{}")).StatusCode.ShouldBe(HttpStatusCode.NotFound, "the digest a database reader holds is not a key");
        (await PostAnonymouslyAsync($"/api/workflows/callbacks/{WorkflowWaitKinds.WithheldBearerToken}", "{}")).StatusCode.ShouldBe(HttpStatusCode.NotFound, "the URL a previous-version SPA builds from the placeholder resumes nothing");

        var resumed = await PostAnonymouslyAsync(path, """{"status":"done"}""");
        resumed.StatusCode.ShouldBe(HttpStatusCode.OK, "the revealed URL is the one the external system POSTs to, with no session");
        await _factory.JobClient.DrainAsync();

        (await RunStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Success);
        (await PostAnonymouslyAsync(path, "{}")).StatusCode.ShouldBe(HttpStatusCode.NotFound, "the wait is resolved; its URL resumes nothing more");
        (await SendAsync(HttpMethod.Post, RevealPath(runId, waitId), world.MemberId, world.TeamId)).StatusCode.ShouldBe(HttpStatusCode.NotFound, "nothing is pending to reveal");
    }

    [Fact]
    public async Task Nothing_the_database_holds_opens_the_ciphertext_a_reveal_reads()
    {
        // A database or backup reader holds the wait row AND the Data Protection key ring, which sits unencrypted in the same
        // database (data_protection_keys) under the public application name. The token's ciphertext must not open with
        // anything in there — the key that opens it is the one the deployment's environment holds. Once the follow-up
        // release blanks the token column, this ciphertext is the only readable copy of the URL.
        var world = await SeedWorldAsync();
        var runId = await LaunchCallbackRunAsync(world);
        await _factory.JobClient.DrainAsync();

        var waitId = JsonDocument.Parse(await ReadOkAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}/pending-wait", world.MemberId, world.TeamId)).RootElement.GetProperty("wait").GetProperty("id").GetGuid();
        var path = JsonDocument.Parse(await ReadOkAsync(HttpMethod.Post, RevealPath(runId, waitId), world.MemberId, world.TeamId)).RootElement.GetProperty("path").GetString()!;
        var token = path["/api/workflows/callbacks/".Length..];
        var (storedToken, _, ciphertext) = await ReadRowAsADatabaseReaderAsync(waitId);

        // Expand step: the token column keeps the token for one release so a previous-version API pod — which compares it
        // verbatim — still resumes the URL during the rollout and after a rollback. The follow-up blanks it; flip this then.
        storedToken.ShouldBe(token);
        ciphertext.ShouldNotBeNull("precondition: the row carries a ciphertext for the reveal");
        ciphertext.ShouldNotContain(token);
        (await TryOpenWithTheDatabaseKeyRingAsync(ciphertext)).ShouldNotBe(token, "the database's own Data Protection key ring must not open the callback token");
        _factory.Services.GetRequiredService<IVariableValueEncryption>().Decrypt(Convert.FromBase64String(ciphertext)).ShouldBe(token, "the reveal opens it with the deployment's environment-held key — so the measurement above is of a real ciphertext");
    }

    // ─── World ──────────────────────────────────────────────────────────────────

    private sealed record World(Guid TeamId, Guid OwnerId, Guid MemberId, Guid ViewerId);

    private static string RevealPath(Guid runId, Guid waitId) => $"/api/workflows/runs/{runId}/waits/{waitId}/callback-url";

    private async Task<World> SeedWorldAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var teamId = Guid.NewGuid();
        var suffix = teamId.ToString("N")[..8];
        db.Team.Add(new Team { Id = teamId, Slug = $"cb-{suffix}", Name = "Callback", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        Guid Member(TeamRole role)
        {
            var userId = Guid.NewGuid();
            db.User.Add(new User { Id = userId, SecurityStamp = TestToken.SeedStamp, Email = $"cb-{role}-{suffix}@test.local", Name = $"cb {role} {suffix}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
            db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = role, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
            return userId;
        }

        var world = new World(teamId, Member(TeamRole.Owner), Member(TeamRole.Member), Member(TeamRole.Viewer));
        await db.SaveChangesAsync();

        return world;
    }

    /// <summary>The Owner authors trigger → flow.wait_callback → terminal over HTTP and launches it.</summary>
    private async Task<Guid> LaunchCallbackRunAsync(World world)
    {
        var workflow = new
        {
            name = $"cb-{Guid.NewGuid():N}"[..20],
            definition = new
            {
                schemaVersion = 1,
                nodes = new[]
                {
                    new { id = "start", typeKey = "trigger.manual", config = new { }, inputs = new { } },
                    new { id = "cb", typeKey = "flow.wait_callback", config = new { }, inputs = new { } },
                    new { id = "end", typeKey = "builtin.terminal", config = new { }, inputs = new { } },
                },
                edges = new[] { new { from = "start", to = "cb" }, new { from = "cb", to = "end" } },
            },
            activations = Array.Empty<object>(),
            enabled = true,
        };

        var workflowId = JsonDocument.Parse(await ReadOkAsync(HttpMethod.Post, "/api/workflows", world.OwnerId, world.TeamId, workflow)).RootElement.GetProperty("id").GetGuid();

        return JsonDocument.Parse(await ReadOkAsync(HttpMethod.Post, $"/api/workflows/{workflowId}/run", world.OwnerId, world.TeamId, new { })).RootElement.GetProperty("runId").GetGuid();
    }

    /// <summary>What a backup holds: the row, read with nothing but a connection to the database.</summary>
    private async Task<(string Token, string? Digest, string? Ciphertext)> ReadRowAsADatabaseReaderAsync(Guid waitId)
    {
        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT token, token_digest, token_ciphertext FROM workflow_run_wait WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", waitId);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();

        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    /// <summary>
    /// The key ring the database itself holds, opened the way the app opens it, under the purpose callback tokens were once
    /// protected with. Null when it cannot open the value.
    /// </summary>
    private async Task<string?> TryOpenWithTheDatabaseKeyRingAsync(string ciphertext)
    {
        var services = new ServiceCollection();
        services.AddDbContext<CodeSpaceDbContext>(options => options.UseNpgsql(_factory.ConnectionString));
        services.AddDataProtection().PersistKeysToDbContext<CodeSpaceDbContext>().SetApplicationName(CodeSpaceDataProtection.ApplicationName);
        await using var provider = services.BuildServiceProvider();

        try
        {
            return provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("CodeSpace.Workflows.CallbackToken.v1").Unprotect(ciphertext);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return null;
        }
    }

    private async Task<WorkflowRunStatus> RunStatusAsync(Guid runId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.Status).SingleAsync();
    }

    private async Task<HttpResponseMessage> PostAnonymouslyAsync(string path, string body) =>
        await _factory.CreateClient().PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));

    private async Task<string> ReadOkAsync(HttpMethod method, string url, Guid userId, Guid teamId, object? body = null)
    {
        var response = await SendAsync(method, url, userId, teamId, body);
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue($"{method} {url} answered {(int)response.StatusCode}: {text}");
        return text;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Guid userId, Guid teamId, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(userId, TestToken.SeedStamp));
        request.Headers.Add("X-Team-Id", teamId.ToString());
        if (body != null) request.Content = JsonContent.Create(body);

        return await _factory.CreateClient().SendAsync(request);
    }
}
