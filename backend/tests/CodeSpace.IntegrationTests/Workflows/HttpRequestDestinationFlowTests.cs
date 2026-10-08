using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.OutboundHttp;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Settings.OutboundHttp;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// http.request's destination guard through the whole engine: a workflow created by the mediator, a run seeded with
/// an untrusted trigger payload, <see cref="IWorkflowEngine.ExecuteRunAsync"/> resolving <c>{{trigger.body}}</c> into
/// the URL, the real <see cref="HttpRequestNode"/> sending through the PRODUCTION guarded-client registration, and the
/// outcome read back from <c>workflow_run_node</c> and the ledger. The responders are raw loopback sockets that count
/// accepted connections, standing in for a metadata endpoint or an admin port on the worker.
///
/// <para>Each test runs the engine in a child scope whose <see cref="IHttpClientFactory"/> carries its own committed
/// allowlist (the fixture's root admits 127.0.0.1 for the suite's other loopback-calling tests), so a refusal here is
/// the guard's and not an artefact of the suite's configuration. The proxy rows build their guard with a loopback
/// responder as the operator's proxy and a stubbed resolver, so no lookup or connection leaves the machine.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class HttpRequestDestinationFlowTests
{
    private const string MetadataPath = "/latest/meta-data/iam/security-credentials/role";
    private const string FakeCredential = "{\"AccessKeyId\":\"FAKE-AKID\",\"Token\":\"FAKE-LOOPBACK-SECRET\"}";

    private readonly PostgresFixture _fixture;

    public HttpRequestDestinationFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("2130706433")]
    [InlineData("[::1]")]
    [InlineData("[::ffff:127.0.0.1]")]
    public async Task A_loopback_url_built_from_trigger_text_fails_the_node_without_reaching_loopback(string host)
    {
        await using var metadata = LoopbackResponder.Start(_ => LoopbackResponder.Ok(FakeCredential));

        var runId = await RunHttpWorkflowAsync("{{trigger.body}}", $"http://{host}:{metadata.Port}{MetadataPath}");

        var node = await ReadCallNodeAsync(runId);
        node.Status.ShouldBe(NodeStatus.Failure);
        node.Error.ShouldNotBeNull().ShouldContain("Refused to connect");
        node.OutputsJson.ShouldNotContain("FAKE-LOOPBACK-SECRET");

        (await ReadExternalCallErrorAsync(runId)).ShouldContain("Refused to connect", customMessage: "the ledger's external_call.failed must say the destination was refused, so the run-detail page explains the failure");
        metadata.Accepted.ShouldBe(0, $"no socket may reach the loopback responder through http://{host}");
    }

    [Fact]
    public async Task A_redirect_to_loopback_from_an_allowlisted_host_fails_the_node_at_the_redirect_hop()
    {
        await using var metadata = LoopbackResponder.Start(_ => LoopbackResponder.Ok(FakeCredential));
        await using var front = LoopbackResponder.Start(_ => LoopbackResponder.Redirect($"http://127.0.0.1:{metadata.Port}{MetadataPath}"));

        var runId = await RunHttpWorkflowAsync($"http://localhost:{front.Port}/{{{{trigger.body}}}}", "feature-x", "localhost");

        var node = await ReadCallNodeAsync(runId);
        node.Status.ShouldBe(NodeStatus.Failure);
        node.Error.ShouldNotBeNull().ShouldContain("Refused to connect to '127.0.0.1'");

        front.Requests.Count.ShouldBe(1, "the allowlisted first hop was made");
        metadata.Accepted.ShouldBe(0, "the redirect target is loopback and not allowlisted — no socket may reach it");
    }

    [Fact]
    public async Task An_allowlisted_internal_destination_still_answers_in_full()
    {
        await using var service = LoopbackResponder.Start(_ => LoopbackResponder.Ok(FakeCredential));

        var runId = await RunHttpWorkflowAsync("{{trigger.body}}", $"http://127.0.0.1:{service.Port}{MetadataPath}", "127.0.0.0/8");

        var node = await ReadCallNodeAsync(runId);
        node.Status.ShouldBe(NodeStatus.Success, node.Error);

        var outputs = JsonDocument.Parse(node.OutputsJson).RootElement;
        outputs.GetProperty("status").GetInt32().ShouldBe(200);
        outputs.GetProperty("body").GetProperty("Token").GetString().ShouldBe("FAKE-LOOPBACK-SECRET", "an operator-admitted destination's response reaches the node outputs as before");
        service.Accepted.ShouldBe(1);
    }

    [Fact]
    public async Task A_public_destination_reached_through_the_operators_proxy_answers_in_full()
    {
        // A worker whose only route out is HTTP(S)_PROXY: the public-resolving name is checked on the worker, the proxy
        // fetches it, and the answer reaches the node outputs. 203.0.113.10 is TEST-NET-3 — public to the guard, routed
        // nowhere — so nothing here could reach a real host even if the proxy were bypassed.
        await using var proxy = LoopbackResponder.Start(_ => LoopbackResponder.Ok("{\"via\":\"operator-proxy\"}"));

        var runId = await RunHttpWorkflowAsync("{{trigger.body}}", "http://api.example.test/v1/items", ProxiedGuard(proxy));

        var node = await ReadCallNodeAsync(runId);
        node.Status.ShouldBe(NodeStatus.Success, node.Error);
        JsonDocument.Parse(node.OutputsJson).RootElement.GetProperty("body").GetProperty("via").GetString().ShouldBe("operator-proxy");
        proxy.Requests.Single().ShouldStartWith("GET http://api.example.test/v1/items HTTP/1.1");
    }

    [Fact]
    public async Task An_internal_destination_the_proxy_would_carry_fails_the_node_before_the_proxy_is_asked()
    {
        await using var proxy = LoopbackResponder.Start(_ => LoopbackResponder.Ok(FakeCredential));

        var runId = await RunHttpWorkflowAsync("{{trigger.body}}", $"http://metadata.example.test{MetadataPath}", ProxiedGuard(proxy));

        var node = await ReadCallNodeAsync(runId);
        node.Status.ShouldBe(NodeStatus.Failure);
        node.Error.ShouldNotBeNull().ShouldContain("Refused to connect to 'metadata.example.test'");
        node.OutputsJson.ShouldNotContain("FAKE-LOOPBACK-SECRET");
        proxy.Accepted.ShouldBe(0, "the proxy must never be asked to fetch the metadata address");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>The production registration from a committed allowlist of <paramref name="allowed"/>, as the host builds it.</summary>
    private Task<Guid> RunHttpWorkflowAsync(string urlTemplate, string triggerBody, params string[] allowed) => RunHttpWorkflowAsync(urlTemplate, triggerBody, services => services.AddGuardedHttpClient(nameof(HttpRequestNode), CommittedAllowlist(allowed)));

    private Task<Guid> RunHttpWorkflowAsync(string urlTemplate, string triggerBody, OutboundDestinationGuard guard) => RunHttpWorkflowAsync(urlTemplate, triggerBody, services => services.AddGuardedHttpClient(nameof(HttpRequestNode), guard));

    private async Task<Guid> RunHttpWorkflowAsync(string urlTemplate, string triggerBody, Action<IServiceCollection> registerGuardedClient)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateHttpWorkflowAsync(teamId, userId, urlTemplate);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: JsonSerializer.Serialize(new { body = triggerBody }));

        var services = new ServiceCollection();
        registerGuardedClient(services);

        await using var guarded = services.BuildServiceProvider();
        using var scope = _fixture.BeginScope(b => b.RegisterInstance(guarded.GetRequiredService<IHttpClientFactory>()).As<IHttpClientFactory>());

        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);

        return runId;
    }

    private static IConfiguration CommittedAllowlist(string[] allowed) => new ConfigurationBuilder()
        .AddInMemoryCollection(allowed.Select((entry, i) => KeyValuePair.Create($"{OutboundHttpAllowlistSetting.ConfigurationKey}:{i}", (string?)entry)))
        .Build();

    /// <summary>Nothing allowlisted; <paramref name="proxy"/> as the operator's HTTP(S)_PROXY; two stubbed names, one public and one the metadata address.</summary>
    private static OutboundDestinationGuard ProxiedGuard(LoopbackResponder proxy) => new(OutboundDestinationAllowlist.Empty, new WebProxy($"http://127.0.0.1:{proxy.Port}"), (host, _) => host switch
    {
        "api.example.test" => Task.FromResult(new[] { IPAddress.Parse("203.0.113.10") }),
        "metadata.example.test" => Task.FromResult(new[] { IPAddress.Parse("169.254.169.254") }),
        _ => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)),
    });

    private async Task<Guid> CreateHttpWorkflowAsync(Guid teamId, Guid userId, string urlTemplate)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<MediatR.IMediator>();

        var def = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.pr.opened", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "call",  TypeKey = "http.request", Config = WorkflowsTestSeed.Json("""{"timeoutSeconds":5}"""), Inputs = WorkflowsTestSeed.Json(JsonSerializer.Serialize(new { url = urlTemplate, method = "GET" })) },
                new() { Id = "end",   TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start", To = "call" },
                new() { From = "call", To = "end" },
            },
        };

        return await mediator.Send(new CodeSpace.Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "http-destination-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = def,
            Activations = new List<CodeSpace.Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task<CodeSpace.Core.Persistence.Entities.WorkflowRunNode> ReadCallNodeAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "call");
    }

    private async Task<string> ReadExternalCallErrorAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();

        var failed = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking().SingleAsync(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.ExternalCallFailed);

        return JsonDocument.Parse(failed.PayloadJson).RootElement.GetProperty("error").GetString() ?? "";
    }
}
