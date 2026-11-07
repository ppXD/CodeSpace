using CodeSpace.Messages.Failures;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Launch.Exceptions;

/// <summary>
/// The resolved route cannot honour a launch control the operator set, so the launch stops before any session or run
/// exists rather than run without it. Carries each refused control with its reason — the same dispositions the route
/// preview reports for this input — so every caller can say what to change.
/// </summary>
public sealed class TaskLaunchControlRefusedException : Exception, IFailure
{
    private readonly IReadOnlyList<LaunchControlDisposition> _refused;

    public TaskLaunchControlRefusedException(IReadOnlyList<LaunchControlDisposition> refused) : base(string.Join(" ", refused.Select(d => $"{d.Control}: {d.Reason}"))) { _refused = refused; }

    public FailureKind Kind => FailureKind.Unprocessable;
    public string Code => FailureCodes.TaskLaunchControlRefused;
    public IReadOnlyDictionary<string, object?> Details => new Dictionary<string, object?> { ["controls"] = _refused };
}
