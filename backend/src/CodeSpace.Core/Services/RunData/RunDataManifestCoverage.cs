using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.RunData;

/// <summary>The facets a new engine walk states as existing-but-unstated, whose producers then advance before/after durable capture.</summary>
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
