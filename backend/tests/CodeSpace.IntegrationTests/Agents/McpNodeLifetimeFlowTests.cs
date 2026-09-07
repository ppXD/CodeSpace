using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>Real UDS endpoint, production authority/registry/node adapter, actual PostgreSQL, and a test extension node that exposes execution-scoped identities and database lifetime without changing any authority decision.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class McpNodeLifetimeFlowTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Concurrent_connections_get_distinct_node_databases_but_the_same_real_run_identity()
    {
        var probe = new InvocationProbe(2);
        await using var host = await OpenAsync(probe);
        await using var first = await WireClient.ConnectAsync(host.Connect);
        await using var second = await WireClient.ConnectAsync(host.Connect);
        var replies = await Task.WhenAll(first.CallAsync(1, new { mode = "success" }), second.CallAsync(2, new { mode = "success" })).WaitAsync(TimeSpan.FromSeconds(20));
        foreach (var reply in replies) reply.GetProperty("isError").GetBoolean().ShouldBeFalse(reply.GetRawText());
        probe.Entries.Count.ShouldBe(2);
        probe.Entries.Select(entry => entry.Db.ContextId.InstanceId).Distinct().Count().ShouldBe(2, "one run's shared catalog must not share execution DbContexts between simultaneous tool calls");
        probe.Entries.ShouldAllBe(entry => entry.UserId == host.UserId && entry.TeamId == host.TeamId && entry.CallTeamId == host.TeamId);
        await AssertDisposedAsync(probe);
        host.Endpoint.ObservedToolCalls.ShouldBe(2);
    }

    [Fact]
    public async Task A_model_cannot_replace_the_run_team_and_fresh_membership_revocation_still_prevents_invocation()
    {
        var probe = new InvocationProbe(1);
        await using var host = await OpenAsync(probe);
        var (foreignTeam, _) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        await using var client = await WireClient.ConnectAsync(host.Connect);
        var first = await client.CallAsync(1, new { mode = "success", teamId = foreignTeam });
        first.GetProperty("isError").GetBoolean().ShouldBeFalse(first.GetRawText());
        probe.Entries.Single().CallTeamId.ShouldBe(host.TeamId, "the adapter must retain the authenticated run's team, never a model-authored teamId");
        await AssertDisposedAsync(probe);
        using (var revoke = fixture.BeginScope())
            await revoke.Resolve<CodeSpaceDbContext>().TeamMembership.Where(member => member.TeamId == host.TeamId && member.UserId == host.UserId).ExecuteDeleteAsync();
        var denied = await client.CallAsync(2, new { mode = "success" });
        denied.GetProperty("isError").GetBoolean().ShouldBeTrue();
        denied.GetProperty("structuredContent").GetProperty("code").GetString().ShouldBe("agent.authority_denied");
        denied.GetProperty("structuredContent").GetProperty("retryable").GetBoolean().ShouldBeFalse();
        probe.Entries.Count.ShouldBe(1, "authority revocation must stop the call before a child node scope executes");
    }

    [Fact]
    public async Task A_builtin_node_called_over_the_socket_cannot_resolve_a_foreign_repository()
    {
        await using var host = await OpenAsync(new InvocationProbe(1));
        var (foreignTeam, _) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        var repositoryId = Guid.NewGuid();
        using (var seed = fixture.BeginScope())
        {
            var db = seed.Resolve<CodeSpaceDbContext>();
            var providerId = Guid.NewGuid();
            db.ProviderInstance.Add(new ProviderInstance { Id = providerId, TeamId = foreignTeam, Provider = ProviderKind.Git, DisplayName = "foreign", BaseUrl = "https://foreign.invalid" });
            db.Repository.Add(new Repository { Id = repositoryId, TeamId = foreignTeam, ProviderInstanceId = providerId, ExternalId = repositoryId.ToString(), NamespacePath = "private", Name = "foreign-repo", FullPath = "private/foreign-repo", DefaultBranch = "main", CloneUrlHttps = "https://foreign.invalid/private/foreign-repo.git", WebUrl = "https://foreign.invalid/private/foreign-repo" });
            await db.SaveChangesAsync();
        }

        await using var client = await WireClient.ConnectAsync(host.Connect);
        var foreign = await client.CallAsync(1, new { repositoryId, teamId = foreignTeam, command = "must-never-execute" }, "agent.run_command");
        var missingId = Guid.NewGuid();
        var missing = await client.CallAsync(2, new { repositoryId = missingId, teamId = foreignTeam, command = "must-never-execute" }, "agent.run_command");
        foreign.GetProperty("isError").GetBoolean().ShouldBeTrue();
        missing.GetProperty("isError").GetBoolean().ShouldBeTrue();
        var foreignText = foreign.GetProperty("content")[0].GetProperty("text").GetString().ShouldNotBeNull();
        var missingText = missing.GetProperty("content")[0].GetProperty("text").GetString().ShouldNotBeNull();
        foreignText.ShouldContain($"Repository {repositoryId} not found.", customMessage: "the real builtin node must refuse at the tenant lookup before any clone or command");
        foreignText.Replace(repositoryId.ToString(), "id").ShouldBe(missingText.Replace(missingId.ToString(), "id"), "foreign and missing repositories must have indistinguishable failure shapes");
        foreignText.ShouldNotContain("foreign.invalid");
        host.Endpoint.ObservedToolCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Tool_failure_preserves_the_error_and_disposes_the_invocation_database_while_the_endpoint_stays_alive()
    {
        var probe = new InvocationProbe(1);
        await using var host = await OpenAsync(probe);
        await using var client = await WireClient.ConnectAsync(host.Connect);
        var reply = await client.CallAsync(1, new { mode = "failure" });
        reply.GetProperty("isError").GetBoolean().ShouldBeTrue();
        reply.GetRawText().ShouldContain("node-lifetime-fault");
        probe.Entries.Count.ShouldBe(1);
        await AssertDisposedAsync(probe);
        host.Connects.TryConnect(host.RunId, out _).ShouldBeTrue("per-call disposal must not dispose the owning endpoint or its run scope");
    }

    [Fact]
    public async Task Endpoint_cancellation_disposes_the_in_flight_node_scope_without_reporting_success()
    {
        var probe = new InvocationProbe(1);
        await using var host = await OpenAsync(probe);
        await using var client = await WireClient.ConnectAsync(host.Connect);
        var call = client.CallAsync(1, new { mode = "cancel" });
        await probe.WaitingForCancellation.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await host.Endpoint.DisposeAsync();
        try { (await call.WaitAsync(TimeSpan.FromSeconds(5))).GetProperty("isError").GetBoolean().ShouldBeTrue(); }
        catch (IOException) { /* endpoint teardown closes the real socket before a response can be sent */ }
        probe.CancellationObserved.ShouldBeTrue();
        probe.Entries.Count.ShouldBe(1);
        await AssertDisposedAsync(probe);
        host.Connects.TryConnect(host.RunId, out _).ShouldBeFalse();
        File.Exists(host.Connect.SocketPath).ShouldBeFalse();
    }

    private async Task<EndpointHost> OpenAsync(InvocationProbe probe)
    {
        Socket.OSSupportsUnixDomainSockets.ShouldBeTrue("this integration proof requires the real Unix socket transport");
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        var identity = fixture.BeginScopeAs(userId, teamId);
        ILifetimeScope? owner = null;
        try
        {
            var task = new AgentTask { Goal = "verify scoped node calls", Harness = "test", Autonomy = AgentAutonomyLevel.Unleashed, Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Unleashed) };
            var runId = (await identity.Resolve<IAgentRunService>().CreateAsync(task, teamId, null, null, cancellationToken: CancellationToken.None)).Id;
            owner = identity.BeginLifetimeScope(builder => builder.Register(context => new ScopeProbeNode(new ProbeBinding(context.Resolve<CodeSpaceDbContext>(), context.Resolve<ICurrentUser>().Id, context.Resolve<ICurrentTeam>().Id, probe))).As<INodeRuntime>().InstancePerLifetimeScope());
            var registry = owner.Resolve<IAgentToolRegistry>();
            var connects = owner.Resolve<IAgentMcpConnectRegistry>();
            var path = $"/tmp/cs-node-scope-{Guid.NewGuid():N}.sock";
            var token = Guid.NewGuid().ToString("N");
            var endpoint = new AgentMcpEndpoint(runId, registry, AgentAutonomyLevel.Unleashed, teamId, SecretRedactor.None, path, token, connects, owner.Resolve<IServiceScopeFactory>().CreateScope(), CancellationToken.None, NullLogger.Instance);
            return new EndpointHost(new EndpointBinding(endpoint, identity, owner, connects), new RunBinding(runId, userId, teamId, new AgentMcpConnect(path, token)));
        }
        catch { owner?.Dispose(); identity.Dispose(); throw; }
    }

    private static async Task AssertDisposedAsync(InvocationProbe probe)
    {
        foreach (var entry in probe.Entries)
            await Should.ThrowAsync<ObjectDisposedException>(() => entry.Db.Team.CountAsync());
    }

    private sealed record EndpointBinding(AgentMcpEndpoint Endpoint, ILifetimeScope Identity, ILifetimeScope Owner, IAgentMcpConnectRegistry Connects);
    private sealed record RunBinding(Guid RunId, Guid UserId, Guid TeamId, AgentMcpConnect Connect);
    private sealed class EndpointHost(EndpointBinding binding, RunBinding run) : IAsyncDisposable
    {
        public AgentMcpEndpoint Endpoint => binding.Endpoint;
        public Guid RunId => run.RunId;
        public Guid UserId => run.UserId;
        public Guid TeamId => run.TeamId;
        public AgentMcpConnect Connect => run.Connect;
        public IAgentMcpConnectRegistry Connects => binding.Connects;
        public async ValueTask DisposeAsync()
        {
            await binding.Endpoint.DisposeAsync();
            await binding.Owner.DisposeAsync();
            await binding.Identity.DisposeAsync();
        }
    }

    private sealed record ProbeBinding(CodeSpaceDbContext Db, Guid? UserId, Guid? TeamId, InvocationProbe Probe);
    private sealed record InvocationEntry(CodeSpaceDbContext Db, Guid? UserId, Guid? TeamId, Guid CallTeamId);
    private sealed class InvocationProbe(int concurrentCalls)
    {
        public ConcurrentBag<InvocationEntry> Entries { get; } = new();
        public TaskCompletionSource WaitingForCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; set; }
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task EnterAsync(InvocationEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            if (Entries.Count == concurrentCalls) _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
    }

    private sealed class ScopeProbeNode(ProbeBinding binding) : INodeRuntime
    {
        public const string Key = "test.node_scope";
        public string TypeKey => Key;
        public NodeManifest Manifest { get; } = new() { DisplayName = "Node scope probe", Category = "Test", Kind = NodeKind.Regular, IsAgentToolEligible = true, ConfigSchema = SchemaBuilder.EmptyObject(), InputSchema = SchemaBuilder.EmptyObject(), OutputSchema = SchemaBuilder.EmptyObject() };
        public async Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
        {
            var callTeamId = context.Scope.Sys[SystemScopeKeys.TeamId].GetGuid();
            await binding.Probe.EnterAsync(new InvocationEntry(binding.Db, binding.UserId, binding.TeamId, callTeamId), cancellationToken);
            var value = await binding.Db.Database.SqlQuery<int>($"SELECT 42 AS \"Value\" FROM pg_sleep(0.1)").SingleAsync(cancellationToken);
            var mode = context.Inputs["mode"].GetString();
            if (mode == "failure") throw new InvalidOperationException("node-lifetime-fault");
            if (mode == "cancel")
            {
                binding.Probe.WaitingForCancellation.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { binding.Probe.CancellationObserved = true; throw; }
            }
            return NodeResult.Ok(new Dictionary<string, JsonElement> { ["value"] = JsonSerializer.SerializeToElement(value) });
        }
    }

    private sealed class WireClient(Socket socket, NetworkStream stream, StreamReader reader, StreamWriter writer) : IAsyncDisposable
    {
        public static async Task<WireClient> ConnectAsync(AgentMcpConnect connect)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(connect.SocketPath));
            var stream = new NetworkStream(socket, ownsSocket: false);
            var reader = new StreamReader(stream, new UTF8Encoding(false));
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            await writer.WriteLineAsync(connect.Token);
            return new WireClient(socket, stream, reader, writer);
        }
        public async Task<JsonElement> CallAsync(int id, object arguments, string name = ScopeProbeNode.Key)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name, arguments } }));
            var reply = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)) ?? throw new EndOfStreamException("The endpoint closed the socket before a reply.");
            return JsonDocument.Parse(reply).RootElement.GetProperty("result").Clone();
        }
        public async ValueTask DisposeAsync()
        {
            reader.Dispose();
            await writer.DisposeAsync();
            await stream.DisposeAsync();
            socket.Dispose();
        }
    }
}
