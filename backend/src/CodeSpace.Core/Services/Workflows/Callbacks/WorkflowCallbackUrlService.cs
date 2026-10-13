using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Callbacks;

public sealed class WorkflowCallbackUrlService : IWorkflowCallbackUrlService, IScopedDependency
{
    /// <summary>The callback route on this API, ahead of the token. Pinned against <c>WorkflowCallbacksController</c>'s route by a test, so the URL handed out is the one the route serves.</summary>
    public const string RoutePrefix = "/api/workflows/callbacks/";

    private readonly CodeSpaceDbContext _db;
    private readonly WorkflowCallbackTokenSealer _sealer;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<WorkflowCallbackUrlService> _logger;

    public WorkflowCallbackUrlService(CodeSpaceDbContext db, WorkflowCallbackTokenSealer sealer, ICurrentUser currentUser, ILogger<WorkflowCallbackUrlService> logger)
    {
        _db = db;
        _sealer = sealer;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<WorkflowCallbackUrl> RevealAsync(Guid runId, Guid waitId, Guid teamId, CancellationToken cancellationToken)
    {
        var wait = await LoadPendingCallbackAsync(runId, waitId, teamId, cancellationToken).ConfigureAwait(false);

        var token = _sealer.Open(wait.Token, wait.TokenCiphertext) ?? throw new WorkflowCallbackUrlUnrecoverableException(runId, waitId);

        RecordReveal(runId, waitId);

        return new WorkflowCallbackUrl { WaitId = waitId, Path = RoutePrefix + token };
    }

    /// <summary>Tenancy and state in one read: a wait on another team's run, of another kind, or no longer pending is the same 404.</summary>
    private async Task<StoredCallbackToken> LoadPendingCallbackAsync(Guid runId, Guid waitId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.Id == waitId && w.RunId == runId && w.WaitKind == WorkflowWaitKinds.Callback && w.Status == WorkflowWaitStatuses.Pending)
            .Where(w => _db.WorkflowRun.Any(r => r.Id == runId && r.TeamId == teamId))
            .Select(w => new StoredCallbackToken(w.Token, w.TokenCiphertext))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No pending callback wait {waitId} on run {runId} in team {teamId}.");

    /// <summary>
    /// The only durable account of a reveal. Warning rather than Information because the question it answers is asked after
    /// an incident — "who could have resumed that run" — and an Information line is the first thing a retention policy drops.
    /// </summary>
    private void RecordReveal(Guid runId, Guid waitId) =>
        _logger.LogWarning(
            "Callback URL for wait {WaitId} on run {RunId} was revealed to user {UserId} ({UserName}). Anyone holding it can resume the run without signing in.",
            waitId, runId, _currentUser.Id, _currentUser.Name);

    private sealed record StoredCallbackToken(string Token, string? TokenCiphertext);
}
