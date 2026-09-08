using CodeSpace.Messages.Failures;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.RoutePreview.Exceptions;

/// <summary>
/// The auto router found ambiguity or risky side effects that require an operator to choose an effort tier before
/// execution. Carries the exact route and its registry-derived options so every caller can render the same decision
/// without reconstructing policy. A route preview is advice and integrity binding; possessing it is not consent.
/// </summary>
public sealed class TaskRouteConfirmationRequiredException : Exception, IFailure
{
    private readonly RoutePlan _route;

    public TaskRouteConfirmationRequiredException(RoutePlan route) : base("Choose an effort tier before launching this ambiguous or potentially risky task.") { _route = route; }

    public FailureKind Kind => FailureKind.Unprocessable;
    public string Code => FailureCodes.TaskRouteConfirmationRequired;
    public IReadOnlyDictionary<string, object?> Details => new Dictionary<string, object?> { ["route"] = _route };
}
