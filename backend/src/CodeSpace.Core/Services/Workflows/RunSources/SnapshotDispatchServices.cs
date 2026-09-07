using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Services.Workflows.Dispatch;

namespace CodeSpace.Core.Services.Workflows.RunSources;

public sealed class SnapshotDispatchServices : IScopedDependency
{
    public SnapshotDispatchServices(IWorkflowRunDispatcher dispatcher, IPostCommitActions postCommit)
    {
        Dispatcher = dispatcher;
        PostCommit = postCommit;
    }
    public IWorkflowRunDispatcher Dispatcher { get; }
    public IPostCommitActions PostCommit { get; }
}
