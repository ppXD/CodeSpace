using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Dtos.ModelCredentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Agents;

/// <summary>
/// A team's model key is write-only to every member — nobody reads it back, and nobody can make the platform carry it
/// somewhere it was not entered for. Driven as a client drives it: the real HTTP surface (routing, JWT, X-Team-Id,
/// model binding, the controller, the GlobalExceptionFilter, MediatR) over real Postgres, with loopback gateways that
/// record every connection they accept and echo the credentials they are presented.
///
/// <para>Fidelity: HIGH. The production <c>ModelCredentialService</c>, the production reflector over its production
/// named client, the production engine and <c>OpenAiClient</c> — only the gateways are stubs, and they are real sockets.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class ModelKeyExposureE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;
    private readonly TenantHarness _tenants;

    public ModelKeyExposureE2ETests(TaskLaunchApiFactory factory)
    {
        _factory = factory;
        _tenants = new TenantHarness(factory);
    }

    [Fact]
    public async Task An_admin_cannot_repoint_a_credential_to_take_its_key_somewhere_else()
    {
        var key = Canary("OWNER-KEY");
        await using var legit = LoopbackResponder.Start(_ => ModelList());
        await using var attacker = LoopbackResponder.Start(_ => ModelList());
        var world = await SeedAsync();
        var id = await AddCredentialAsync(world.Owner, world.Team, key, $"http://127.0.0.1:{legit.Port}/v1");

        var repoint = await _tenants.SendAsync(HttpMethod.Put, $"/api/model-credentials/{id}", world.Admin, world.Team, new { displayName = "gateway", baseUrl = $"http://127.0.0.1:{attacker.Port}/v1" });
        repoint.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await TenantHarness.DescribeAsync(repoint));
        (await repoint.Content.ReadAsStringAsync()).ShouldContain("model_endpoint_key_required");

        var refresh = await _tenants.SendAsync(HttpMethod.Post, $"/api/model-credentials/{id}/models/refresh", world.Admin, world.Team);
        refresh.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(refresh));

        attacker.Accepted.ShouldBe(0, "the refused repoint stored nothing, so the refresh after it went to the endpoint the key was entered for — check the credential's base_url row if this fails");
        legit.Requests.ShouldContain(head => head.Contains($"Bearer {key}"), "fixture check: the refresh really presented the key, to its own endpoint");

        var moved = await _tenants.SendAsync(HttpMethod.Put, $"/api/model-credentials/{id}", world.Admin, world.Team, new { displayName = "gateway", apiKey = "sk-admins-own-key-0123456789", baseUrl = $"http://127.0.0.1:{attacker.Port}/v1" });
        moved.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(moved));
        (await _tenants.SendAsync(HttpMethod.Post, $"/api/model-credentials/{id}/models/refresh", world.Admin, world.Team)).StatusCode.ShouldBe(HttpStatusCode.OK);

        attacker.Requests.ShouldNotBeEmpty("fixture check: a repoint WITH a key is the supported way to move a credential");
        attacker.Requests.ShouldAllBe(head => !head.Contains(key), "the new endpoint only ever sees the key entered with the move");
    }

    [Theory]
    [InlineData(TeamRole.Owner)]
    [InlineData(TeamRole.Admin)]
    [InlineData(TeamRole.Member)]
    [InlineData(TeamRole.Viewer)]
    public async Task Every_member_reads_the_base_url_with_its_credentials_masked(TeamRole role)
    {
        var password = Canary("URL-PASSWORD");
        var queryKey = Canary("URL-QUERY-KEY");
        var world = await SeedAsync();
        await AddCredentialAsync(world.Owner, world.Team, Canary("KEY"), $"https://svc:{password}@gw.example/v1?api-key={queryKey}&api-version=2024-01-01");

        var list = await _tenants.SendAsync(HttpMethod.Get, "/api/model-credentials", world.As(role), world.Team);
        var body = await list.Content.ReadAsStringAsync();

        list.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        body.ShouldNotContain(password);
        body.ShouldNotContain(queryKey);
        JsonSerializer.Deserialize<ModelCredentialSummary[]>(body, Json)!.ShouldHaveSingleItem().BaseUrl.ShouldBe("https://***:***@gw.example/v1?api-key=***&api-version=2024-01-01");
    }

    [Fact]
    public async Task An_edit_form_that_sends_back_what_it_was_shown_keeps_the_stored_url()
    {
        var stored = $"https://svc:{Canary("URL-PASSWORD")}@gw.example/v1";
        var world = await SeedAsync();
        var id = await AddCredentialAsync(world.Owner, world.Team, Canary("KEY"), stored);
        var shown = (await (await _tenants.SendAsync(HttpMethod.Get, "/api/model-credentials", world.Admin, world.Team)).Content.ReadFromJsonAsync<ModelCredentialSummary[]>(Json))!.Single();

        var save = await _tenants.SendAsync(HttpMethod.Put, $"/api/model-credentials/{id}", world.Admin, world.Team, new { displayName = "renamed", apiKey = (string?)null, baseUrl = shown.BaseUrl });

        save.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(save));
        (await _tenants.DbAsync(db => db.ModelCredential.AsNoTracking().SingleAsync(c => c.Id == id))).BaseUrl.ShouldBe(stored, "the masked round-trip keeps the credentials the platform needs to reach the gateway");
    }

    [Fact]
    public async Task A_viewer_reads_a_failed_run_without_the_key_the_gateway_echoed()
    {
        var key = Canary("RUN-KEY");
        await using var gateway = LoopbackResponder.Start(EchoTheBearer);
        var world = await SeedAsync();
        var id = await AddCredentialAsync(world.Owner, world.Team, key, $"http://127.0.0.1:{gateway.Port}/v1");
        (await _tenants.SendAsync(HttpMethod.Post, $"/api/model-credentials/{id}/models", world.Owner, world.Team, new { modelId = "gpt-echo" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var runId = await LaunchLlmRunAsync(world);
        await _factory.JobClient.DrainAsync();

        gateway.Requests.ShouldContain(head => head.Contains(key), "fixture check: the run presented the key to the gateway, whose 401 echoed it");
        foreach (var path in new[] { $"/api/workflows/runs/{runId}", $"/api/workflows/runs/{runId}/records", $"/api/workflows/runs/{runId}/timeline" })
        {
            var response = await _tenants.SendAsync(HttpMethod.Get, path, world.Viewer, world.Team);
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{path}: {body}");
            body.ShouldNotContain(key, Case.Sensitive, $"{path} handed a Viewer the echoed model key");
        }
    }

    private async Task<Guid> LaunchLlmRunAsync(World world)
    {
        var workflow = new
        {
            name = $"echo-{Guid.NewGuid():N}"[..20], enabled = true, activations = Array.Empty<object>(),
            definition = new
            {
                schemaVersion = 1,
                nodes = new object[]
                {
                    new { id = "start", typeKey = "trigger.manual", label = "start", config = new { }, inputs = new { } },
                    new { id = "llm", typeKey = "llm.complete", label = "llm", config = new { provider = "Custom", model = "gpt-echo" }, inputs = new { userPrompt = "hello" } },
                    new { id = "end", typeKey = "builtin.terminal", label = "end", config = new { }, inputs = new { } },
                },
                edges = new object[] { new { from = "start", to = "llm" }, new { from = "llm", to = "end" } },
            },
        };

        var created = await _tenants.SendAsync(HttpMethod.Post, "/api/workflows", world.Owner, world.Team, workflow);
        created.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(created));
        var workflowId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var launched = await _tenants.SendAsync(HttpMethod.Post, $"/api/workflows/{workflowId}/run", world.Owner, world.Team, new { });
        launched.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(launched));

        return (await launched.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();
    }

    private async Task<Guid> AddCredentialAsync(Guid owner, Guid team, string key, string baseUrl)
    {
        var added = await _tenants.SendAsync(HttpMethod.Post, "/api/model-credentials", owner, team, new { provider = "Custom", displayName = "gateway", apiKey = key, baseUrl });
        added.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(added));

        return (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<World> SeedAsync()
    {
        var owner = await _tenants.AccountAsync();
        var admin = await _tenants.AccountAsync();
        var member = await _tenants.AccountAsync();
        var viewer = await _tenants.AccountAsync();
        var team = await _tenants.TeamAsync((owner, TeamRole.Owner), (admin, TeamRole.Admin), (member, TeamRole.Member), (viewer, TeamRole.Viewer));

        return new World(team, owner, admin, member, viewer);
    }

    private static string Canary(string label) => $"CANARY-{label}-{Guid.NewGuid():N}";

    private static string ModelList() => Respond(HttpStatusCode.OK, """{"object":"list","data":[{"id":"m1","object":"model"}]}""");

    /// <summary>A gateway that rejects the key it was presented and quotes it back, the way a careless 401 does.</summary>
    private static string EchoTheBearer(string head)
    {
        var presented = head.Split("\r\n").FirstOrDefault(line => line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) ?? "";

        return Respond(HttpStatusCode.Unauthorized, JsonSerializer.Serialize(new { error = new { message = $"Invalid API key provided: {presented}", type = "invalid_request_error" } }));
    }

    private static string Respond(HttpStatusCode status, string body) => $"HTTP/1.1 {(int)status} {status}\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private sealed record World(Guid Team, Guid Owner, Guid Admin, Guid Member, Guid Viewer)
    {
        public Guid As(TeamRole role) => role switch { TeamRole.Owner => Owner, TeamRole.Admin => Admin, TeamRole.Member => Member, _ => Viewer };
    }
}
