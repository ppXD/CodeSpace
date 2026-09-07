using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Backends;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class WorkflowNodeLifetimeFlowTests(PostgresFixture fixture)
{
    [Fact]
    public void Production_nodes_belong_to_the_resolving_scope_while_the_catalog_remains_identical()
    {
        using var first = fixture.BeginScope();
        using var second = fixture.BeginScope();
        var one = first.Resolve<INodeRegistry>();
        var two = second.Resolve<INodeRegistry>();
        first.Resolve<INodeRegistry>().ShouldBeSameAs(one);
        two.All.Select(node => node.TypeKey).Order().ShouldBe(one.All.Select(node => node.TypeKey).Order());
        foreach (var node in one.All.Where(node => node.GetType().Assembly == typeof(CodeSpaceModule).Assembly))
            two.Resolve(node.TypeKey).ShouldNotBeSameAs(node, $"production {node.TypeKey} must not retain another execution scope's dependencies");
    }

    [Fact]
    public async Task Parallel_command_nodes_use_distinct_databases_and_preserve_their_real_actor_team_and_artifacts()
    {
        var root = Directory.CreateTempSubdirectory("cs-node-lifetime-").FullName;
        var probe = new ConcurrentWriteProbe();
        var first = await SeedRunAsync('a');
        var second = await SeedRunAsync('b');
        try
        {
            await Task.WhenAll(ExecuteAsync(first), ExecuteAsync(second)).WaitAsync(TimeSpan.FromSeconds(40));
            probe.Entries.Count.ShouldBe(2, "both actual node artifact writes must reach the per-execution backend through production DI");
            probe.Entries.Select(entry => entry.Db.ContextId.InstanceId).Distinct().Count().ShouldBe(2, "overlapping node stores must not share EF's non-thread-safe context");
            probe.Entries.Select(entry => (entry.UserId, entry.TeamId)).ShouldBe(new[] { ((Guid?)first.UserId, (Guid?)first.TeamId), ((Guid?)second.UserId, (Guid?)second.TeamId) }, ignoreOrder: true);
            foreach (var entry in probe.Entries)
                await Should.ThrowAsync<ObjectDisposedException>(() => entry.Db.WorkflowArtifact.CountAsync());

            using var verify = fixture.BeginScope(builder => builder.RegisterInstance(new LocalFileArtifactBlobBackend(root)).As<IArtifactBlobBackend>());
            var db = verify.Resolve<CodeSpaceDbContext>();
            var artifacts = verify.Resolve<IArtifactStore>();
            foreach (var seeded in new[] { first, second })
            {
                (await db.WorkflowRun.AsNoTracking().SingleAsync(run => run.Id == seeded.RunId)).Status.ShouldBe(WorkflowRunStatus.Success);
                var node = await db.WorkflowRunNode.AsNoTracking().SingleAsync(node => node.RunId == seeded.RunId && node.NodeId == "command");
                node.Status.ShouldBe(NodeStatus.Success, node.Error);
                var output = JsonDocument.Parse(node.OutputsJson).RootElement;
                output.GetProperty("exitCode").GetInt32().ShouldBe(0);
                output.GetProperty("stdoutCaptureComplete").GetBoolean().ShouldBeTrue();
                var artifactId = output.GetProperty("stdoutArtifactId").GetGuid();
                var bytes = (await artifacts.GetBytesAsync(seeded.TeamId, artifactId, CancellationToken.None)).ShouldNotBeNull().Bytes;
                Encoding.UTF8.GetString(bytes).ShouldBe(new string(seeded.Content, 16384));
                var otherTeam = seeded == first ? second.TeamId : first.TeamId;
                (await artifacts.GetBytesAsync(otherTeam, artifactId, CancellationToken.None)).ShouldBeNull();
            }
        }
        finally { Directory.Delete(root, recursive: true); }

        async Task ExecuteAsync(SeededRun seeded)
        {
            using var identity = fixture.BeginScopeAs(seeded.UserId, seeded.TeamId);
            using var execution = identity.BeginLifetimeScope(builder => builder.Register(context => new ObservedBlobBackend(new BlobBinding(new LocalFileArtifactBlobBackend(root), context.Resolve<CodeSpaceDbContext>(), context.Resolve<ICurrentUser>().Id, context.Resolve<ICurrentTeam>().Id, probe))).As<IArtifactBlobBackend>().InstancePerLifetimeScope());
            await execution.Resolve<IWorkflowEngine>().ExecuteRunAsync(seeded.RunId, CancellationToken.None);
        }
    }

    private async Task<SeededRun> SeedRunAsync(char content)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        using var author = fixture.BeginScopeAs(userId, teamId);
        var workflowId = await author.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "node-lifetime", Enabled = true, Activations = new List<WorkflowActivationInput>(),
            Definition = new WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "command", TypeKey = "agent.run_command", Config = WorkflowsTestSeed.EmptyJson(), Inputs = JsonSerializer.SerializeToElement(new { command = "/bin/sh", args = new[] { "-c", $"head -c 16384 /dev/zero | tr '\\000' {content}" }, maxOutputChars = 1024 }) },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<EdgeDefinition> { new() { From = "start", To = "command" }, new() { From = "command", To = "end" } },
            },
        });
        return new SeededRun(teamId, userId, await WorkflowsTestSeed.SeedManualRunAsync(fixture, workflowId, teamId), content);
    }

    private sealed record SeededRun(Guid TeamId, Guid UserId, Guid RunId, char Content);
    private sealed record BlobBinding(IArtifactBlobBackend Backend, CodeSpaceDbContext Db, Guid? UserId, Guid? TeamId, ConcurrentWriteProbe Probe);
    private sealed class ConcurrentWriteProbe
    {
        public ConcurrentBag<BlobBinding> Entries { get; } = new();
        private readonly TaskCompletionSource _bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task EnterAsync(BlobBinding binding, CancellationToken cancellationToken)
        {
            Entries.Add(binding);
            if (Entries.Count == 2) _bothEntered.TrySetResult();
            await _bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
    }
    private sealed class ObservedBlobBackend(BlobBinding binding) : IArtifactBlobBackend
    {
        public async Task<string> WriteAsync(string sha256, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            await binding.Probe.EnterAsync(binding, cancellationToken);
            return await binding.Backend.WriteAsync(sha256, bytes, cancellationToken);
        }
        public Task<bool> ExistsAsync(string storageUrl, CancellationToken cancellationToken) => binding.Backend.ExistsAsync(storageUrl, cancellationToken);
        public Task<byte[]> ReadAsync(string storageUrl, CancellationToken cancellationToken) => binding.Backend.ReadAsync(storageUrl, cancellationToken);
        public Task<ArtifactBlobRange> ReadRangeAsync(string storageUrl, long offset, int length, CancellationToken cancellationToken) => binding.Backend.ReadRangeAsync(storageUrl, offset, length, cancellationToken);
    }
}
