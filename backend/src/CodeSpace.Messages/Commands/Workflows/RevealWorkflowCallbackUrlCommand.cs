using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// Hand back the URL of a run's pending <c>Callback</c> wait.
///
/// <para>A command rather than a query, though it reads: the URL's token is a bearer credential — the callback route is
/// anonymous, so whoever holds it resumes the run with a body of their choosing — so handing it out is an ACTION, and it
/// leaves a log line naming who took it. Queries are gated by membership alone, which would hand it to Viewers.</para>
///
/// <para><see cref="TeamPermissions.RunsControl"/> because holding the URL is the power to resume the run, which is what
/// that permission gates (a Member can already force-resolve the same wait with a body of their own through
/// <see cref="ReissueWaitCommand"/>).</para>
///
/// <para>Tenancy: the wait's run must belong to the caller's current team, and the wait must still be a pending
/// <c>Callback</c> — anything else is 404.</para>
/// </summary>
public sealed record RevealWorkflowCallbackUrlCommand : ICommand<WorkflowCallbackUrl>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.RunsControl;

    public required Guid RunId { get; init; }

    public required Guid WaitId { get; init; }
}
