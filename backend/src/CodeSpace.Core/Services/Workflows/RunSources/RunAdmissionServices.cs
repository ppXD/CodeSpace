using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Authority;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Core.Services.Workflows.Lifecycle;

namespace CodeSpace.Core.Services.Workflows.RunSources;

public sealed class RunAdmissionServices : IScopedDependency
{
    public RunAdmissionServices(IRunRecordLogger records, IWorkSessionService sessions, IModeProfileRegistry modes, ExecutionAuthorityService authority)
    {
        Records = records;
        Sessions = sessions;
        Modes = modes;
        Authority = authority;
    }
    public IRunRecordLogger Records { get; }
    public IWorkSessionService Sessions { get; }
    public IModeProfileRegistry Modes { get; }
    public ExecutionAuthorityService Authority { get; }
}
