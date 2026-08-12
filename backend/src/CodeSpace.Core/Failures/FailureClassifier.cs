using CodeSpace.Messages.Failures;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CodeSpace.Core.Failures;

/// <summary>
/// Turns any exception into what the system knows about it: a kind, a code, what the caller may be
/// told, and whatever they need in order to act.
///
/// <para>This exists because the classification used to live inside the MVC exception filter, which
/// meant only HTTP callers got one. The same <c>ProviderRateLimitedException</c> was a 429 the client
/// retried on the API and a permanent, unretried job failure in the background — one exception, two
/// verdicts, decided by which door it came through. Everything that has to answer for a failure now
/// asks the same question here and gets the same answer.</para>
///
/// <para>Static and pure on purpose: it is consulted from an MVC filter, a MediatR pipeline action,
/// and background workers with no scope of their own, and a failure path is the worst place to need
/// a container.</para>
/// </summary>
public static class FailureClassifier
{
    private const string PostgresUniqueViolation = "23505";

    /// <summary>Shown in place of any message the caller has not earned. Never varies — a varying mask leaks by timing and shape.</summary>
    public const string MaskedMessage = "An unexpected error occurred.";

    public static FailureClassification Classify(Exception exception)
    {
        // An exception that knows its own meaning is always believed. This is the path every domain
        // type should be on; the arms below are what is left until they all are.
        if (exception is IFailure failure) return FromFailure(exception, failure);

        return exception switch
        {
            UnauthorizedAccessException => new(FailureKind.Unauthenticated, FailureCodes.Unauthorized, "Authentication required."),

            // The domain's 50-site not-found signal. The message is dropped, not masked for secrecy but
            // because it names internal identifiers and the caller already knows what they asked for.
            KeyNotFoundException => new(FailureKind.NotFound, FailureCodes.NotFound, "The requested resource was not found."),

            DbUpdateException db when IsUniqueViolation(db) => new(FailureKind.Conflict, FailureCodes.DuplicateResource, "That already exists."),

            // ArgumentException is always a caller-shaped fault, so its message is safe to surface.
            ArgumentException argument => new(FailureKind.Invalid, FailureCodes.InvalidRequest, argument.Message),

            // InvalidOperationException is NOT: it is thrown 176 times across services for both "you
            // asked for something contradictory" and "an invariant this system holds did not hold".
            // Until those are separated it is classified as the caller's fault, because that is the
            // behaviour callers already depend on — but its message is surfaced knowingly, and
            // DomainExceptionConventionTests counts the remaining sites so the debt stays visible.
            InvalidOperationException invalid => new(FailureKind.Invalid, FailureCodes.InvalidRequest, invalid.Message),

            _ => new(FailureKind.Internal, FailureCodes.Internal, MaskedMessage),
        };
    }

    private static FailureClassification FromFailure(Exception exception, IFailure failure)
    {
        var message = failure.Kind == FailureKind.Internal ? MaskedMessage : failure.ClientMessage ?? exception.Message;

        return new FailureClassification(failure.Kind, failure.Code, message, failure.Details, (failure as IUpstreamStatus)?.UpstreamStatus);
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresUniqueViolation };
}

/// <summary>
/// What every consumer of a failure gets to work with, whichever door the failure came through.
/// </summary>
/// <param name="Kind">What the caller should do about it.</param>
/// <param name="Code">The stable wire name, from <see cref="FailureCodes"/>.</param>
/// <param name="ClientMessage">Safe to show. Already masked when the kind says it must be.</param>
/// <param name="Details">Fields the caller needs in order to act.</param>
/// <param name="UpstreamStatus">The status a third party returned, when the failure came from one. Not our transport's status — see <see cref="IUpstreamStatus"/>.</param>
public sealed record FailureClassification(
    FailureKind Kind,
    string Code,
    string ClientMessage,
    IReadOnlyDictionary<string, object?>? Details = null,
    int? UpstreamStatus = null);
