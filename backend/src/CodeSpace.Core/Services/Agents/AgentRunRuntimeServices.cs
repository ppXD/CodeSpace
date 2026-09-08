using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Workflows.Artifacts;

namespace CodeSpace.Core.Services.Agents;

public sealed class AgentRunRuntimeServices : IScopedDependency
{
    public AgentRunRuntimeServices(IAdmissionController admission, ISandboxRunnerRegistry runners, IArtifactOffloader offloader, IToolCallLedgerService ledger, ICompletionContractStore contracts, Capture.INativeRecordPlane nativeRecords)
    {
        Admission = admission;
        Runners = runners;
        Offloader = offloader;
        Ledger = ledger;
        Contracts = contracts;
        NativeRecords = nativeRecords;
    }
    public IAdmissionController Admission { get; }
    public ISandboxRunnerRegistry Runners { get; }
    public IArtifactOffloader Offloader { get; }
    public IToolCallLedgerService Ledger { get; }
    public ICompletionContractStore Contracts { get; }
    public Capture.INativeRecordPlane NativeRecords { get; }
}
