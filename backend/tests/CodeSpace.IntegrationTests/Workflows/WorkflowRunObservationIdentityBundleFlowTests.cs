using System.Data.Common;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Display;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>Real-Postgres contract for one request's exact, body-blind Workflow Run identity bundle.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class WorkflowRunObservationIdentityBundleFlowTests
{
    private readonly PostgresFixture _fixture;

    public WorkflowRunObservationIdentityBundleFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task One_scope_coalesces_the_exact_team_run_key_and_never_selects_hot_bodies()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, WorkflowRunStatus.Running);
        var recorder = new ReadCommandRecorder();

        using var scope = ReadScope(recorder);
        var bundle = scope.Resolve<IWorkflowRunObservationIdentityBundle>();
        var first = await bundle.GetAsync(teamId, runId, CancellationToken.None);
        var second = await bundle.GetAsync(teamId, runId, CancellationToken.None);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => bundle.GetAsync(teamId, runId, CancellationToken.None)));

        first.ShouldNotBeNull();
        first!.RunId.ShouldBe(runId);
        first.Status.ShouldBe(WorkflowRunStatus.Running);
        second.ShouldBe(first);
        concurrent.ShouldAllBe(value => ReferenceEquals(value, first));
        var sql = recorder.Commands.ShouldHaveSingleItem("one request scope admits one exact (team, run) identity query");
        sql.ShouldContain("team_id");
        sql.ShouldContain("status");
        sql.ShouldNotContain("JOIN", Case.Insensitive);
        sql.ShouldNotContain("outputs_jsonb", Case.Insensitive);
        sql.ShouldNotContain("normalized_payload_json", Case.Insensitive);
        sql.ShouldNotContain("definition_snapshot_jsonb", Case.Insensitive);
        sql.ShouldNotContain("workflow_run_record", Case.Insensitive);
    }

    [Fact]
    public async Task Foreign_and_missing_are_conflated_but_a_new_scope_observes_a_new_status()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (foreignTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, WorkflowRunStatus.Running);

        using (var scope = _fixture.BeginScope())
        {
            var bundle = scope.Resolve<IWorkflowRunObservationIdentityBundle>();
            (await bundle.GetAsync(foreignTeamId, runId, CancellationToken.None)).ShouldBeNull();
            (await bundle.GetAsync(teamId, Guid.NewGuid(), CancellationToken.None)).ShouldBeNull();
            (await bundle.GetAsync(teamId, runId, CancellationToken.None))!.Status.ShouldBe(WorkflowRunStatus.Running);
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            await db.WorkflowRun.Where(run => run.Id == runId).ExecuteUpdateAsync(update => update.SetProperty(run => run.Status, WorkflowRunStatus.Success));
        }

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IWorkflowRunObservationIdentityBundle>().GetAsync(teamId, runId, CancellationToken.None))!.Status.ShouldBe(WorkflowRunStatus.Success,
                "the cache is request-scoped; mutable status never survives into a later request");
    }

    [Fact]
    public async Task Database_fault_propagates_and_is_not_translated_to_missing()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, WorkflowRunStatus.Running);
        using var scope = ReadScope(new ThrowingReadInterceptor());

        var error = await Should.ThrowAsync<IOException>(() => scope.Resolve<IWorkflowRunObservationIdentityBundle>().GetAsync(teamId, runId, CancellationToken.None));

        error.Message.ShouldBe("identity backend unavailable");
    }

    [Fact]
    public async Task One_consumer_cancellation_does_not_cancel_the_request_owned_shared_read()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, WorkflowRunStatus.Running);
        var blocker = new BlockingReadInterceptor();
        using var request = new CancellationTokenSource();
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestAborted = request.Token } };
        await using var scope = ReadScope(blocker, accessor);
        var bundle = scope.Resolve<IWorkflowRunObservationIdentityBundle>();
        using var firstConsumer = new CancellationTokenSource();

        var first = bundle.GetAsync(teamId, runId, firstConsumer.Token);
        await blocker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = bundle.GetAsync(teamId, runId, CancellationToken.None);
        firstConsumer.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => first);
        blocker.Release.TrySetResult();
        var identity = await second.WaitAsync(TimeSpan.FromSeconds(5));

        identity.ShouldNotBeNull();
        identity!.RunId.ShouldBe(runId);
        blocker.Commands.ShouldBe(1, "both consumers must share one request-owned query");
    }

    private ILifetimeScope ReadScope(DbCommandInterceptor interceptor) => _fixture.BeginScope(builder =>
    {
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention().AddInterceptors(interceptor).Options;
        builder.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
    });

    private ILifetimeScope ReadScope(DbCommandInterceptor interceptor, IHttpContextAccessor accessor) => _fixture.BeginScope(builder =>
    {
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention().AddInterceptors(interceptor).Options;
        builder.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
        builder.RegisterInstance(accessor).As<IHttpContextAccessor>().SingleInstance();
    });

    private async Task<Guid> SeedRunAsync(Guid teamId, WorkflowRunStatus status)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user",
            ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}", RequestMetadataJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Snapshot,
            Status = status, OutputsJson = "{}", CreatedDate = now, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
        return runId;
    }

    private sealed class ReadCommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ThrowingReadInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            throw new IOException("identity backend unavailable");
    }

    private sealed class BlockingReadInterceptor : DbCommandInterceptor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Commands { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }
}
