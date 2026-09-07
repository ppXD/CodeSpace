using Autofac;
using CodeSpace.Core.Services.Tasks.Effort;
using CodeSpace.Core.Services.Tasks.Projection;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;

namespace CodeSpace.E2ETests.Infrastructure;

/// <summary>Observes the real router and injects one rollback fault. No model replies or CLI execution are simulated.</summary>
public sealed class TaskRouteSnapshotApiFactory : TaskLaunchApiFactory
{
    public RouteSnapshotCalls Calls { get; } = new();

    protected override void ConfigureContractTestServices(ContainerBuilder builder)
    {
        builder.RegisterInstance(Calls);
        builder.RegisterDecorator<CountingRouter, IEffortRouter>();
        builder.RegisterDecorator<FaultableSnapshotFactory, ITaskRunSnapshotFactory>();
    }

    public sealed class RouteSnapshotCalls
    {
        private int _routes;
        public int Routes => Volatile.Read(ref _routes);
        public bool FailNextStage { get; set; }
        public void CountRoute() => Interlocked.Increment(ref _routes);
    }

    public sealed class CountingRouter(IEffortRouter inner, RouteSnapshotCalls calls) : IEffortRouter
    {
        public Task<RoutePlan> RouteAsync(EffortRouteRequest request, CancellationToken ct)
        {
            calls.CountRoute();
            return inner.RouteAsync(request, ct);
        }
    }

    public sealed class FaultableSnapshotFactory(ITaskRunSnapshotFactory inner, RouteSnapshotCalls calls) : ITaskRunSnapshotFactory
    {
        public async Task<TaskRunHandle> CreateAndRunAsync(TaskBuildContext context, Guid teamId, Guid actorUserId, SessionAssignment? session, CancellationToken cancellationToken)
        {
            var handle = await inner.CreateAndRunAsync(context, teamId, actorUserId, session, cancellationToken);
            if (calls.FailNextStage)
            {
                calls.FailNextStage = false;
                throw new IOException("Injected interruption after run staging, before route consumption commits.");
            }
            return handle;
        }
    }
}
