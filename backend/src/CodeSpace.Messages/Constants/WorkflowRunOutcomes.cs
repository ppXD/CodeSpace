namespace CodeSpace.Messages.Constants;

/// <summary>Durable workflow outcome vocabulary for terminal states that need more truth than the coarse run status carries.</summary>
public static class WorkflowRunOutcomes
{
    /// <summary>The workflow delivered a usable result, but one or more mapped branches failed.</summary>
    public const string PartialFailure = "PartialFailure";

    /// <summary>A continue-on-error map completed structurally, but every one of its branches failed.</summary>
    public const string AllBranchesFailed = "AllBranchesFailed";
}
