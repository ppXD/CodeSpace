using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeSpace.Api.Controllers;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.ExternalTriggers;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Webhooks;

[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class ExternalTriggerEndpointE2ETests(WebhookApiFactory factory) : IClassFixture<WebhookApiFactory>
{
    private const string BODY = """{"service":"a"}""";
    private sealed record Seed(Guid UserId, Guid TeamId, Guid WorkflowId, string Slug, string Key);

    [Fact]
    public async Task An_unknown_slug_is_404()
    {
        using var response = await PostAsync($"/api/ext/missing-{Guid.NewGuid():N}/run", "{}");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ext_wrong")]
    [InlineData("Bearer ")]
    public async Task A_missing_or_wrong_key_is_401_before_validation(string? authorization)
    {
        var seed = await SeedAsync();
        using var response = await PostAsync(Url(seed), "not json", authorization: authorization);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await ShouldHaveNoRequestsAsync(seed);
    }

    [Theory]
    [InlineData("bearer")]
    [InlineData("mixed-bearer")]
    [InlineData("api-key")]
    [InlineData("query")]
    [InlineData("empty-bearer-fallback")]
    [InlineData("wrong-scheme-fallback")]
    [InlineData("api-key-before-query")]
    public async Task The_supported_key_forms_start_a_run(string form)
    {
        var seed = await SeedAsync();
        var authorization = form switch
        {
            "bearer" => "Bearer " + seed.Key,
            "mixed-bearer" => "bEaReR " + seed.Key,
            "empty-bearer-fallback" => "Bearer ",
            "wrong-scheme-fallback" => "Basic not-a-key",
            _ => null,
        };
        var apiKey = form is "api-key" or "empty-bearer-fallback" or "wrong-scheme-fallback" or "api-key-before-query" ? seed.Key : null;
        var url = Url(seed) + (form == "query" ? "?key=" + seed.Key : form == "api-key-before-query" ? "?key=wrong" : "");
        using var response = await PostAsync(url, BODY, authorization, apiKey);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await TenantHarness.DescribeAsync(response));
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("deduplicated").GetBoolean().ShouldBeFalse();
        var runs = await RunsAsync(seed);
        payload.GetProperty("runId").GetGuid().ShouldBe(runs.ShouldHaveSingleItem());
    }

    [Fact]
    public async Task A_bearer_key_takes_precedence_over_other_key_forms()
    {
        var seed = await SeedAsync();
        using var response = await PostAsync(Url(seed) + "?key=" + seed.Key, BODY, "Bearer ext_wrong", seed.Key);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await ShouldHaveNoRequestsAsync(seed);
    }

    [Fact]
    public async Task A_body_over_the_limit_is_413()
    {
        var seed = await SeedAsync();
        using var response = await PostAsync(Url(seed), new string('x', (int)WebhooksController.MaxDeliveryBodyBytes + 1), apiKey: seed.Key);
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        await ShouldHaveNoRequestsAsync(seed);
    }

    [Fact]
    public async Task A_contract_violation_is_400_with_the_ingress_errors_unchanged()
    {
        var seed = await SeedAsync();
        const string INVALID_BODY = """{"service":7}""";
        using var scope = factory.Services.CreateScope();
        var expected = await scope.ServiceProvider.GetRequiredService<IExternalTriggerIngress>().ReceiveAsync(new(seed.Slug, seed.Key, null, INVALID_BODY, false), CancellationToken.None);
        using var response = await PostAsync(Url(seed), INVALID_BODY, apiKey: seed.Key);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ShouldBe(expected.Errors);
        (await response.Content.ReadAsStringAsync()).ShouldContain("$.service");
        await ShouldHaveNoRequestsAsync(seed);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("7")]
    public async Task An_authenticated_malformed_or_non_object_body_is_400_and_writes_nothing(string body)
    {
        var seed = await SeedAsync();
        using var response = await PostAsync(Url(seed), body, apiKey: seed.Key);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ShouldBe(new[] { "$: body must be a JSON object" });
        await ShouldHaveNoRequestsAsync(seed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_invalid_idempotency_key_is_400_and_starts_nothing(bool oversized)
    {
        var seed = await SeedAsync();
        using var response = await PostAsync(Url(seed), BODY, apiKey: seed.Key, idempotencyKey: oversized ? new string('a', 201) : "bad key");
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ShouldBe(new[] { "Idempotency-Key must be at most 200 characters from [A-Za-z0-9._~-]" });
        await ShouldHaveNoRequestsAsync(seed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_absent_or_blank_idempotency_key_does_not_deduplicate(string? idempotencyKey)
    {
        var seed = await SeedAsync();
        for (var i = 0; i < 2; i++)
        {
            using var response = await PostAsync(Url(seed), BODY, apiKey: seed.Key, idempotencyKey: idempotencyKey);
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }
        (await RunsAsync(seed)).Length.ShouldBe(2);
    }

    [Fact]
    public async Task A_deduplicated_delivery_answers_200_with_the_first_run_id()
    {
        var seed = await SeedAsync();
        using var first = await PostAsync(Url(seed), BODY, apiKey: seed.Key, idempotencyKey: "evt-1");
        using var second = await PostAsync(Url(seed), BODY, apiKey: seed.Key, idempotencyKey: "evt-1");
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var accepted = await first.Content.ReadFromJsonAsync<JsonElement>();
        var repeated = await second.Content.ReadFromJsonAsync<JsonElement>();
        repeated.GetProperty("deduplicated").GetBoolean().ShouldBeTrue();
        repeated.GetProperty("runId").GetGuid().ShouldBe(accepted.GetProperty("runId").GetGuid());
        (await RunsAsync(seed)).ShouldHaveSingleItem().ShouldBe(accepted.GetProperty("runId").GetGuid());
    }

    [Fact]
    public async Task An_admission_refusal_is_202_with_a_null_run_id()
    {
        var seed = await SeedAsync();
        await new TenantHarness(factory).DbAsync(db => db.TeamMembership.Where(m => m.TeamId == seed.TeamId).ExecuteDeleteAsync());
        using var response = await PostAsync(Url(seed), BODY, apiKey: seed.Key);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("runId").ValueKind.ShouldBe(JsonValueKind.Null);
        payload.GetProperty("deduplicated").GetBoolean().ShouldBeFalse();
        (await RunsAsync(seed)).ShouldBeEmpty();
    }

    private static string Url(Seed seed) => $"/api/ext/{seed.Slug}/run";

    private async Task<HttpResponseMessage> PostAsync(string url, string body, string? authorization = null, string? apiKey = null, string? idempotencyKey = null)
    {
        using var client = new TenantHarness(factory).Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (authorization != null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        if (apiKey != null) request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        if (idempotencyKey != null) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private async Task<Seed> SeedAsync()
    {
        var harness = new TenantHarness(factory);
        var userId = await harness.AccountAsync();
        var teamId = await harness.TeamAsync((userId, TeamRole.Owner));
        var slug = "alerts-" + Guid.NewGuid().ToString("N");
        var config = new { slug, inputSchema = new { type = "object", properties = new { service = new { type = "string" } }, required = new[] { "service" } } };
        using var response = await harness.SendAsync(HttpMethod.Post, "/api/workflows", userId, teamId, new
        {
            name = slug, enabled = true,
            definition = new
            {
                schemaVersion = 1,
                nodes = new object[]
                {
                    new { id = "trigger", typeKey = "trigger.external", config, inputs = new { } },
                    new { id = "end", typeKey = "builtin.terminal", config = new { }, inputs = new { } },
                },
                edges = new[] { new { from = "trigger", to = "end" } },
            },
            activations = new[] { new { typeKey = "trigger.external", enabled = true, config } },
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(response));
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return await SeedCredentialAsync(userId, teamId, payload.GetProperty("id").GetGuid(), slug);
    }

    private async Task<Seed> SeedCredentialAsync(Guid userId, Guid teamId, Guid workflowId, string slug)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var key = scope.ServiceProvider.GetRequiredService<ExternalTriggerKeySealer>().Mint();
        db.WorkflowExternalTrigger.Add(new WorkflowExternalTrigger
        {
            Id = Guid.NewGuid(), TeamId = teamId, WorkflowId = workflowId, NodeId = "trigger", Slug = slug,
            KeyDigest = key.Digest, KeyCiphertext = key.Ciphertext, CreatedBy = userId, LastModifiedBy = userId,
        });
        await db.SaveChangesAsync();
        return new(userId, teamId, workflowId, slug, key.Key);
    }

    private Task<Guid[]> RunsAsync(Seed seed) => new TenantHarness(factory).DbAsync(db => db.WorkflowRun.AsNoTracking().Where(r => r.WorkflowId == seed.WorkflowId).Select(r => r.Id).ToArrayAsync());

    private async Task ShouldHaveNoRequestsAsync(Seed seed)
    {
        (await RunsAsync(seed)).ShouldBeEmpty();
        (await new TenantHarness(factory).DbAsync(db => db.WorkflowRunRequest.CountAsync(r => r.WorkflowId == seed.WorkflowId))).ShouldBe(0);
    }
}
