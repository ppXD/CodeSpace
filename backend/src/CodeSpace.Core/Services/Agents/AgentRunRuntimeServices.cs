using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Workflows.Artifacts;

namespace CodeSpace.Core.Services.Agents;

public sealed class AgentRunRuntimeServices : IScopedDependency
{
    public AgentRunRuntimeServices(IAdmissionController admission, ISandboxRunnerRegistry runners, IArtifactOffloader offloader, IToolCallLedgerService ledger, ICompletionContractStore contracts)
    {
        Admission = admission;
        Runners = runners;
        Offloader = offloader;
        Ledger = ledger;
        Contracts = contracts;
    }
    public IAdmissionController Admission { get; }
    public ISandboxRunnerRegistry Runners { get; }
    public IArtifactOffloader Offloader { get; }
    public IToolCallLedgerService Ledger { get; }
    public ICompletionContractStore Contracts { get; }
}
