namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// Thrown once a Continue has revived the run past the generation the walk doing the work claimed — at a wave check, at
/// a step's park, or at the staging a node commits itself under <see cref="RunGenerationFence"/> (the supervisor's spawn
/// wave). It unwinds the overtaken walk to <c>WorkflowEngine.RunAfterClaimAsync</c>, which stands the walk down writing
/// nothing: the run belongs to the revived walk.
/// </summary>
public sealed class RunSupersededException : Exception
{
    public RunSupersededException(int claimed) : base($"Run was continued past this walk (claimed generation {claimed}).") { Claimed = claimed; }

    public int Claimed { get; }
}
