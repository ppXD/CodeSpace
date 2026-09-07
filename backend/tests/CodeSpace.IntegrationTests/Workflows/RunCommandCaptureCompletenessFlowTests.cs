using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Backends;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>Production-registered engine and nodes → real command process → filesystem artifact + PostgreSQL gap. The storage override changes only the backend; no test registry or node construction bypasses production dependency lifetimes.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RunCommandCaptureCompletenessFlowTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Captured_excerpt_preserves_the_execution_receipt_and_records_capture_or_storage_gaps(bool refuseStorage, bool rebindStorage)
    {
        const int produced = 2 * SandboxCaptureBudget.DefaultBytes;
        var root = Directory.CreateTempSubdirectory("cs-command-capture-").FullName;
        var blobRoot = Path.Combine(root, "blobs");
        if (refuseStorage) await File.WriteAllTextAsync(blobRoot, "a real file prevents creating the backend directory");
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        Guid workflowId;
        using (var author = fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            workflowId = await author.Resolve<IMediator>().Send(new CreateWorkflowCommand
            {
                Name = "bounded-command-output", Enabled = true, Activations = new List<WorkflowActivationInput>(),
                Definition = Definition($"head -c {produced} /dev/zero | tr '\\000' x; printf execution-receipt >&2; exit 9"),
            });
        }
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(fixture, workflowId, teamId);
        try
        {
            using (var execution = rebindStorage ? ScopeWithBlobRoot(blobRoot) : fixture.BeginScope())
                await execution.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);

            using var verify = rebindStorage ? ScopeWithBlobRoot(blobRoot) : fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(run => run.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);
            var node = await db.WorkflowRunNode.AsNoTracking().SingleAsync(candidate => candidate.RunId == runId && candidate.NodeId == "command");
            node.Status.ShouldBe(NodeStatus.Success, node.Error);
            var output = JsonDocument.Parse(node.OutputsJson).RootElement;
            output.GetProperty("status").GetString().ShouldBe("Failed");
            output.GetProperty("exitCode").GetInt32().ShouldBe(9);
            output.GetProperty("stdoutBytes").GetInt64().ShouldBe(produced);
            output.GetProperty("stdoutBytesIsLowerBound").GetBoolean().ShouldBeFalse();
            output.GetProperty("stdoutCapturedBytes").GetInt64().ShouldBe(SandboxCaptureBudget.DefaultBytes);
            output.GetProperty("stdoutCaptureComplete").GetBoolean().ShouldBeFalse();
            output.GetProperty("stderr").GetString().ShouldBe("execution-receipt");
            output.TryGetProperty("stdoutArtifactId", out _).ShouldBeFalse("the available prefix is never advertised as a full source artifact");
            var hasExcerpt = output.TryGetProperty("stdoutCapturedArtifactId", out var excerpt);
            hasExcerpt.ShouldBe(!refuseStorage);
            if (hasExcerpt)
            {
                var stored = (await verify.Resolve<IArtifactStore>().GetBytesAsync(teamId, excerpt.GetGuid(), CancellationToken.None)).ShouldNotBeNull();
                stored.Bytes.Length.ShouldBe(SandboxCaptureBudget.DefaultBytes);
                Encoding.UTF8.GetString(stored.Bytes).ShouldBe(new string('x', SandboxCaptureBudget.DefaultBytes));
                (await verify.Resolve<IArtifactStore>().GetBytesAsync(Guid.NewGuid(), excerpt.GetGuid(), CancellationToken.None)).ShouldBeNull();
            }
            var gaps = await db.WorkflowRunCaptureGap.AsNoTracking().Where(gap => gap.WorkflowRunId == runId && gap.SubjectKind == WorkflowRunDataOwnerKinds.NodeOutput && gap.SubjectId == "command").ToListAsync();
            gaps.Count.ShouldBe(refuseStorage ? 2 : 1);
            gaps.ShouldContain(gap => gap.ReasonDetail!.Contains("capture is incomplete"));
            gaps.ShouldAllBe(gap => gap.TeamId == teamId && gap.Resolution == CaptureGapResolution.Open);
            (await db.WorkflowRunRecord.CountAsync(record => record.RunId == runId && record.NodeId == "command" && record.RecordType == "node.started")).ShouldBe(1);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private ILifetimeScope ScopeWithBlobRoot(string root)
    {
        return fixture.BeginScope(builder => builder.RegisterInstance(new LocalFileArtifactBlobBackend(root)).As<IArtifactBlobBackend>());
    }

    private static WorkflowDefinition Definition(string script) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "command", TypeKey = "agent.run_command", Config = WorkflowsTestSeed.EmptyJson(), Inputs = JsonSerializer.SerializeToElement(new { command = "/bin/sh", args = new[] { "-c", script }, maxOutputChars = 1024 }) },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "command" }, new() { From = "command", To = "end" } },
    };
}
