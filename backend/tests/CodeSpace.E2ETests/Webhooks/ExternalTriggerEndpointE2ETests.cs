using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeSpace.Api.Controllers;
using CodeSpace.Api.Extensions;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.ExternalTriggers;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Dtos.Workflows;
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
    private sealed record Seed(Guid UserId, Guid TeamId, Guid WorkflowId, Guid TriggerId, string Key);

    [Fact]
    public async Task Metadata_lists_only_public_live_fields_and_allows_viewers()
    {
        var seed = await SeedAsync();
        var harness = new TenantHarness(factory);
        var viewer = await harness.AccountAsync();
        await harness.AddMemberAsync(seed.TeamId, viewer, TeamRole.Viewer);
        using var response = await harness.SendAsync(HttpMethod.Get,
            $"/api/workflows/external-triggers?workflowId={seed.WorkflowId}", viewer, seed.TeamId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(response));
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.EnumerateObject().Select(p => p.Name).ShouldBe(new[] { "triggers" });
        var trigger = payload.GetProperty("triggers").EnumerateArray().ShouldHaveSingleItem();
        trigger.EnumerateObject().Select(p => p.Name).Order().ShouldBe(new[] { "id", "nodeId", "workflowId" });
        trigger.GetProperty("id").GetGuid().ShouldBe(seed.TriggerId);
        trigger.GetProperty("workflowId").GetGuid().ShouldBe(seed.WorkflowId);
        trigger.GetProperty("nodeId").GetString().ShouldBe("trigger");
    }

    [Fact]
    public async Task Metadata_returns_404_for_a_missing_workflow_and_401_for_anonymous_callers()
    {
        var seed = await SeedAsync();
        var harness = new TenantHarness(factory);
        using var missing = await harness.SendAsync(HttpMethod.Get,
            $"/api/workflows/external-triggers?workflowId={Guid.NewGuid()}", seed.UserId, seed.TeamId);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var anonymous = await harness.Client().GetAsync($"/api/workflows/external-triggers?workflowId={seed.WorkflowId}");
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Metadata_returns_an_empty_list_when_the_workflow_has_no_live_trigger()
    {
        var seed = await SeedAsync();
        var harness = new TenantHarness(factory);
        await harness.DbAsync(db => db.WorkflowExternalTrigger.Where(t => t.WorkflowId == seed.WorkflowId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.DeletedDate, DateTimeOffset.UtcNow)));
        using var response = await harness.SendAsync(HttpMethod.Get,
            $"/api/workflows/external-triggers?workflowId={seed.WorkflowId}", seed.UserId, seed.TeamId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(response));
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("triggers").EnumerateArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task Management_reveals_and_rotates_without_changing_the_endpoint()
    {
        var seed = await SeedAsync();
        var harness = new TenantHarness(factory);
        using var reveal = await harness.SendAsync(HttpMethod.Get, $"/api/workflows/external-triggers/{seed.TriggerId}/key", seed.UserId, seed.TeamId);
        reveal.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(reveal));
        reveal.Headers.CacheControl!.Private.ShouldBeTrue();
        reveal.Headers.CacheControl.NoStore.ShouldBeTrue();
        (await reveal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString().ShouldBe(seed.Key);
        using var rotate = await harness.SendAsync(HttpMethod.Post, $"/api/workflows/external-triggers/{seed.TriggerId}/rotate-key", seed.UserId, seed.TeamId);
        rotate.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(rotate));
        rotate.Headers.CacheControl!.Private.ShouldBeTrue();
        rotate.Headers.CacheControl.NoStore.ShouldBeTrue();
        var fresh = (await rotate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString();
        fresh.ShouldNotBeNullOrEmpty();
        fresh.ShouldNotBe(seed.Key);
        using var oldKey = await PostAsync(Url(seed), BODY, apiKey: seed.Key);
        oldKey.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var newUrl = await PostAsync(Url(seed), BODY, apiKey: fresh);
        newUrl.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Theory]
    [InlineData("key", "GET")]
    [InlineData("rotate-key", "POST")]
    public async Task Management_is_team_scoped_and_denies_viewers(string action, string method)
    {
        var seed = await SeedAsync();
        var harness = new TenantHarness(factory);
        var viewer = await harness.AccountAsync();
        await harness.AddMemberAsync(seed.TeamId, viewer, TeamRole.Viewer);
        var url = $"/api/workflows/external-triggers/{seed.TriggerId}/{action}";
        using var denied = await harness.SendAsync(new HttpMethod(method), url, viewer, seed.TeamId, method == "POST" ? new { desiredSlug = "unused" } : null);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var other = await harness.TeamAsync((seed.UserId, TeamRole.Member));
        using var absent = await harness.SendAsync(new HttpMethod(method), url, seed.UserId, other, method == "POST" ? new { desiredSlug = "unused" } : null);
        absent.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var anonymous = await harness.Client().GetAsync($"/api/workflows/external-triggers/{seed.TriggerId}/key");
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Independent_trigger_identities_have_independent_endpoints()
    {
        var first = await SeedAsync();
        var other = await SeedAsync();
        first.TriggerId.ShouldNotBe(other.TriggerId);
        using var firstCall = await PostAsync(Url(first), BODY, apiKey: first.Key);
        using var otherCall = await PostAsync(Url(other), BODY, apiKey: other.Key);
        firstCall.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        otherCall.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

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
    [InlineData("empty-bearer-fallback")]
    [InlineData("wrong-scheme-fallback")]
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
        using var response = await PostAsync(Url(seed), BODY, authorization, apiKey);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await TenantHarness.DescribeAsync(response));
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("deduplicated").GetBoolean().ShouldBeFalse();
        var runs = await RunsAsync(seed);
        payload.GetProperty("runId").GetGuid().ShouldBe(runs.ShouldHaveSingleItem());
    }

    [Fact]
    public async Task A_key_in_the_query_string_is_not_accepted()
    {
        var seed = await SeedAsync();
        using var response = await PostAsync(Url(seed) + "?key=" + seed.Key, BODY);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await ShouldHaveNoRequestsAsync(seed);
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
        using var response = await PostAsync(Url(seed), new string('x', 1_048_577), apiKey: seed.Key);
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        await ShouldHaveNoRequestsAsync(seed);
    }

    [Theory]
    [InlineData(0, HttpStatusCode.Accepted)]
    [InlineData(1, HttpStatusCode.RequestEntityTooLarge)]
    public async Task The_actual_body_byte_limit_is_one_MiB(int extra, HttpStatusCode expected)
    {
        var seed = await SeedAsync();
        var body = BODY + new string(' ', 1_048_576 - Encoding.UTF8.GetByteCount(BODY) + extra);
        using var response = await PostAsync(Url(seed), body, apiKey: seed.Key);
        response.StatusCode.ShouldBe(expected);
        if (extra != 0) await ShouldHaveNoRequestsAsync(seed);
    }

    [Fact]
    public async Task Unknown_retired_missing_and_wrong_keys_have_the_same_public_failure()
    {
        var seed = await SeedAsync();
        using var missing = await PostAsync(Url(seed), "bad");
        using var wrong = await PostAsync(Url(seed), "bad", apiKey: "wrong");
        using var unknown = await PostAsync($"/api/ext/{Guid.NewGuid()}/run", "bad", apiKey: seed.Key);
        await new TenantHarness(factory).DbAsync(db => db.WorkflowExternalTrigger.Where(t => t.Id == seed.TriggerId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.DeletedDate, DateTimeOffset.UtcNow)));
        using var retired = await PostAsync(Url(seed), "bad", apiKey: seed.Key);
        foreach (var response in new[] { missing, wrong, unknown, retired })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            payload.GetProperty("code").GetString().ShouldBe("unauthorized");
            payload.GetProperty("message").GetString().ShouldBe("Invalid external trigger credentials.");
        }
    }

    [Fact]
    public async Task A_contract_violation_is_400_with_the_ingress_errors_unchanged()
    {
        var seed = await SeedAsync();
        const string INVALID_BODY = """{"service":7}""";
        using var scope = factory.Services.CreateScope();
        var expected = await scope.ServiceProvider.GetRequiredService<IExternalTriggerIngress>().ReceiveAsync(new(seed.TriggerId, seed.Key, null, INVALID_BODY, false), CancellationToken.None);
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

    [Fact]
    public async Task An_unknown_slug_is_404_without_reading_the_body()
    {
        using var response = await PostAsync($"/api/ext/missing-{Guid.NewGuid():N}/run", new string('x', 1024 * 1024));
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_validated_endpoint_asks_for_retry_after_when_its_run_budget_is_exhausted()
    {
        var seed = await SeedAsync();
        for (var i = 0; i < ExternalTriggerRunAdmission.PermitsPerWindow; i++)
        {
            using var admitted = await PostAsync(Url(seed), BODY, apiKey: seed.Key);
            admitted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        using var refused = await PostAsync(Url(seed), BODY, apiKey: seed.Key);
        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        refused.Headers.RetryAfter!.Delta.ShouldBe(ExternalTriggerRunAdmission.Window);
    }

    [Fact]
    public async Task The_request_limiter_returns_retry_after_for_canonical_UUID_targets()
    {
        var id = Guid.NewGuid();
        for (var i = 0; i < WebhookIngressRateLimitExtension.PermitsPerWindow; i++)
        {
            using var response = await PostAsync($"/api/ext/{id:D}/run", "{}");
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        using var refused = await PostAsync($"/api/ext/{id.ToString("N").ToUpperInvariant()}/run", "{}");
        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        refused.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().ShouldBe("rate_limited");
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
    public async Task An_authority_refusal_is_403_without_a_run()
    {
        var seed = await SeedAsync();
        await new TenantHarness(factory).DbAsync(db => db.TeamMembership.Where(m => m.TeamId == seed.TeamId).ExecuteDeleteAsync());
        using var response = await PostAsync(Url(seed), BODY, apiKey: seed.Key);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("code").GetString().ShouldBe("forbidden");
        payload.GetProperty("message").GetString().ShouldBe("External trigger execution is forbidden.");
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("membership-revoked");
        (await RunsAsync(seed)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_completion_policy_refusal_is_422_without_a_run()
    {
        var seed = await SeedAsync();
        await new TenantHarness(factory).DbAsync(async db =>
        {
            var workflow = await db.Workflow.SingleAsync(w => w.Id == seed.WorkflowId);
            var graph = JsonSerializer.Deserialize<WorkflowDefinition>(workflow.DefinitionJson, WorkflowJson.Options)!;
            var definition = JsonSerializer.Serialize(graph with { CompletionMode = WorkflowDefinition.CompletionModeEnforced }, WorkflowJson.Options);
            workflow.LatestVersion = 2;
            workflow.DefinitionJson = definition;
            db.WorkflowVersion.Add(new WorkflowVersion
            {
                WorkflowId = seed.WorkflowId,
                Version = 2,
                DefinitionJson = definition,
                DefinitionHash = "legacy",
                CommittedAt = DateTimeOffset.UtcNow,
                CreatedDate = DateTimeOffset.UtcNow,
                CreatedBy = seed.UserId,
            });
            await db.SaveChangesAsync();
            return true;
        });

        using var response = await PostAsync(Url(seed), BODY, apiKey: seed.Key);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("code").GetString().ShouldBe("external_trigger_completion_refused");
        payload.GetProperty("message").GetString().ShouldBe("External trigger execution was refused.");
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("ModeProfileRegistry");
        (await RunsAsync(seed)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refusal_is_audited_once_per_trigger_reason_and_day()
    {
        var seed = await SeedAsync();
        await new TenantHarness(factory).DbAsync(db => db.TeamMembership.Where(m => m.TeamId == seed.TeamId).ExecuteDeleteAsync());

        using (await PostAsync(Url(seed), BODY, apiKey: seed.Key))
        using (await PostAsync(Url(seed), BODY, apiKey: seed.Key))
        using (await PostAsync(Url(seed), BODY, apiKey: seed.Key, idempotencyKey: "first"))
        using (await PostAsync(Url(seed), BODY, apiKey: seed.Key, idempotencyKey: "second"))
        {
            var rows = await new TenantHarness(factory).DbAsync(db => db.WorkflowRunRequest.AsNoTracking()
                .Where(r => r.TeamId == seed.TeamId && r.Status == WorkflowRunRequestStatus.Rejected)
                .Select(r => r.IdempotencyKey).ToArrayAsync());
            rows.ShouldHaveSingleItem().ShouldStartWith("refused:");
        }
    }

    private static string Url(Seed seed) => $"/api/ext/{seed.TriggerId}/run";

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
        var row = await db.WorkflowExternalTrigger.AsNoTracking().SingleAsync(t => t.WorkflowId == workflowId && t.TeamId == teamId && t.DeletedDate == null);
        var key = scope.ServiceProvider.GetRequiredService<ExternalTriggerKeySealer>().Open(row.KeyCiphertext);
        return new(userId, teamId, workflowId, row.Id, key);
    }

    private Task<Guid[]> RunsAsync(Seed seed) => new TenantHarness(factory).DbAsync(db => db.WorkflowRun.AsNoTracking().Where(r => r.WorkflowId == seed.WorkflowId).Select(r => r.Id).ToArrayAsync());

    private async Task ShouldHaveNoRequestsAsync(Seed seed)
    {
        (await RunsAsync(seed)).ShouldBeEmpty();
        (await new TenantHarness(factory).DbAsync(db => db.WorkflowRunRequest.CountAsync(r => r.WorkflowId == seed.WorkflowId))).ShouldBe(0);
    }
}
