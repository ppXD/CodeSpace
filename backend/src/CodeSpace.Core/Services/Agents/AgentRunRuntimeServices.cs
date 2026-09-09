using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Learning;
using CodeSpace.Core.Services.Workflows.Artifacts;

namespace CodeSpace.Core.Services.Agents;

public sealed class AgentRunRuntimeServices : IScopedDependency
{
    public AgentRunRuntimeServices(IAdmissionController admission, ISandboxRunnerRegistry runners, AgentRunDurabilityServices durability, IAgentLessonInjector lessons)
    {
        Admission = admission;
        Runners = runners;
        Durability = durability;
        Lessons = lessons;
    }
    public IAdmissionController Admission { get; }
    public ISandboxRunnerRegistry Runners { get; }
    public AgentRunDurabilityServices Durability { get; }
    public IAgentLessonInjector Lessons { get; }
    public IArtifactOffloader Offloader => Durability.Offloader;
    public IToolCallLedgerService Ledger => Durability.Ledger;
    public ICompletionContractStore Contracts => Durability.Contracts;
    public Capture.INativeRecordPlane NativeRecords => Durability.NativeRecords;
}

public sealed class AgentRunDurabilityServices : IScopedDependency
{
    public AgentRunDurabilityServices(IArtifactOffloader offloader, IToolCallLedgerService ledger, ICompletionContractStore contracts, Capture.INativeRecordPlane nativeRecords)
    {
        Offloader = offloader;
        Ledger = ledger;
        Contracts = contracts;
        NativeRecords = nativeRecords;
    }

    public IArtifactOffloader Offloader { get; }
    public IToolCallLedgerService Ledger { get; }
    public ICompletionContractStore Contracts { get; }
    public Capture.INativeRecordPlane NativeRecords { get; }
}
