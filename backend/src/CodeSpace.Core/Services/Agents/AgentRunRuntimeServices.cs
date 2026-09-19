using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Learning;
using CodeSpace.Core.Services.Workflows.Artifacts;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSpace.Core.Services.Agents;

public sealed class AgentRunRuntimeServices : IScopedDependency
{
    public AgentRunRuntimeServices(IAdmissionController admission, ISandboxRunnerRegistry runners, AgentRunDurabilityServices durability, IAgentLessonInjector lessons, IServiceScopeFactory scopes)
    {
        Admission = admission;
        Runners = runners;
        Durability = durability;
        Lessons = lessons;
        Scopes = scopes;
    }
    public IAdmissionController Admission { get; }
    public ISandboxRunnerRegistry Runners { get; }
    public AgentRunDurabilityServices Durability { get; }
    public IAgentLessonInjector Lessons { get; }

    /// <summary>
    /// Independent DI scopes for work that must NOT join the caller's unit of work. An operator cancel lands a run
    /// Cancelled without the executor's fold, so the run's still-live spend claim is nobody else's to close — and
    /// closing it on the scoped context would put a ledger transaction and its advisory locks inside the cancel
    /// command's own transaction, where one failed settle aborts the whole kill wave after its processes are dead.
    /// </summary>
    public IServiceScopeFactory Scopes { get; }
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
