namespace CodeSpace.Core.Services.Workflows;

/// <summary>
/// Thrown when a from-node rerun (D7) names a node that can't be a valid rerun target: it doesn't exist in the
/// run's frozen definition, or it's a container-internal (map-branch / loop-iteration) node — which v1 reruns
/// only as part of its whole container, never as a body-internal start. Distinct from
/// <see cref="WorkflowValidationException"/> so the global exception filter maps it to 400 with the specific
/// message (which names the offending node / the container to rerun instead) rather than a generic 404.
/// </summary>
public sealed class RerunTargetNotFoundException : Exception
{
    public RerunTargetNotFoundException(string message) : base(message) { }
}
