using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Dispatch;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Rerun;
using CodeSpace.Core.Services.Workflows.RunSources;

namespace CodeSpace.Core.Services.Workflows;

public sealed class WorkflowDefinitionServices : IScopedDependency
{
    public WorkflowDefinitionServices(DefinitionValidator validator, INodeRegistry nodes, ICurrentUser author) { Validator = validator; Nodes = nodes; Author = author; }
    public DefinitionValidator Validator { get; }
    public INodeRegistry Nodes { get; }
    public ICurrentUser Author { get; }
}

public sealed class WorkflowLaunchServices : IScopedDependency
{
    public WorkflowLaunchServices(IRunStarter starter, IRunFromSnapshotStarter snapshots, IWorkflowRunDispatcher dispatcher, IPostCommitActions postCommit, IRunRecordLogger records)
    {
        Starter = starter;
        Snapshots = snapshots;
        Dispatcher = dispatcher;
        PostCommit = postCommit;
        Records = records;
    }
    public IRunStarter Starter { get; }
    public IRunFromSnapshotStarter Snapshots { get; }
    public IWorkflowRunDispatcher Dispatcher { get; }
    public IPostCommitActions PostCommit { get; }
    public IRunRecordLogger Records { get; }
}

public sealed class WorkflowControlServices : IScopedDependency
{
    public WorkflowControlServices(IWorkflowResumeService resume, IAgentRunService agents, IRerunCellSeeder cells, IRunCancellationRegistry cancellation) { Resume = resume; Agents = agents; Cells = cells; Cancellation = cancellation; }
    public IWorkflowResumeService Resume { get; }
    public IAgentRunService Agents { get; }
    public IRerunCellSeeder Cells { get; }
    public IRunCancellationRegistry Cancellation { get; }
}
