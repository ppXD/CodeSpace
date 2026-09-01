using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.RunData;

/// <summary>The facets a new engine walk states as existing-but-unstated, whose producers then advance before/after durable capture.</summary>
public static class RunDataManifestCoverage
{
    /// <summary>The applicability question every pre-0187 run was read against. Never derive legacy history from the deployment's current producer list.</summary>
    public static readonly IReadOnlyList<string> LegacyV1Facets =
    [
        WorkflowRunDataOwnerKinds.ModelCall,
        WorkflowRunDataOwnerKinds.HarnessExecution,
        WorkflowRunDataOwnerKinds.HarnessProcessAttempt,
        WorkflowRunDataOwnerKinds.NativeRecord,
    ];

    public static readonly IReadOnlyList<string> RequiredFacets =
        LegacyV1Facets;
}

public sealed record RunDataManifestInitialization(Guid TeamId, Guid WorkflowRunId);
