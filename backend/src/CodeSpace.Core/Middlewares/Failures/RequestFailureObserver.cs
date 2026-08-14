using CodeSpace.Core.Failures;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Failures;
using MediatR.Pipeline;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Middlewares.Failures;

/// <summary>
/// Sees every exception that escapes a MediatR handler, whichever door the request came through, and
/// records it once with the same classification the caller will be given.
///
/// <para>MediatR's own seam for this (<c>IRequestExceptionAction</c>), registered open-generic so it
/// applies to every request without a single handler knowing it exists. That is the point: before
/// this, an HTTP request's failure was classified by an MVC filter and a recurring job's identical
/// failure was logged as an undifferentiated error, so severity depended on the transport rather than
/// on what went wrong.</para>
///
/// <para>It OBSERVES and never handles. MediatR's sibling seam, <c>IRequestExceptionHandler</c>, can
/// mark an exception handled — and the exception processors sit INSIDE <c>TransactionalBehavior</c>,
/// so an exception marked handled there returns normally through the transaction, which then commits
/// the very work that failed. Nothing here should use that seam; an action has no way to swallow,
/// which is why this is one.</para>
/// </summary>
public sealed class RequestFailureObserver<TRequest, TException> : IRequestExceptionAction<TRequest, TException> where TRequest : notnull where TException : Exception
{
    private readonly ILogger<RequestFailureObserver<TRequest, TException>> _logger;
    private readonly ICurrentUser _currentUser;
    private readonly ICurrentTeam _currentTeam;

    public RequestFailureObserver(ILogger<RequestFailureObserver<TRequest, TException>> logger, ICurrentUser currentUser, ICurrentTeam currentTeam)
    {
        _logger = logger;
        _currentUser = currentUser;
        _currentTeam = currentTeam;
    }

    public Task Execute(TRequest request, TException exception, CancellationToken cancellationToken)
    {
        // Cancellation is the caller leaving, not a failure. Logging it as one turns every user who
        // closes a tab mid-request into an error someone has to triage.
        if (exception is OperationCanceledException) return Task.CompletedTask;

        var failure = FailureClassifier.Classify(exception);

        _logger.Log(
            FailureLogging.SeverityFor(failure.Kind),
            exception,
            "{RequestName} failed: {FailureKind}/{FailureCode} user={UserId} team={TeamId}",
            typeof(TRequest).Name,
            failure.Kind,
            failure.Code,
            _currentUser.Id,
            _currentTeam.Id);

        // Tells the MVC filter this one is already accounted for. Without the mark it cannot tell
        // "recorded here" from "thrown somewhere the pipeline never saw", and it has to assume the
        // latter or those failures reach nobody.
        FailureLogging.MarkLogged(exception);

        return Task.CompletedTask;
    }
}
