using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.RunData;

/// <summary>The facets whose producers declare zero before a new engine walk and then advance before/after durable capture.</summary>
public static class RunDataManifestCoverage
{
    public static readonly IReadOnlyList<string> RequiredFacets =
    [
        WorkflowRunDataOwnerKinds.ModelCall,
        WorkflowRunDataOwnerKinds.HarnessExecution,
        WorkflowRunDataOwnerKinds.HarnessProcessAttempt,
        WorkflowRunDataOwnerKinds.NativeRecord,
    ];
}

public sealed record RunDataManifestInitialization(Guid TeamId, Guid WorkflowRunId);
