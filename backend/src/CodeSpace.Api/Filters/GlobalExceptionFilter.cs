using CodeSpace.Core.Failures;
using CodeSpace.Messages.Failures;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CodeSpace.Api.Filters;

/// <summary>
/// Renders a classified failure as an HTTP response. That is the whole job.
///
/// <para>It used to be the only place that knew what any exception MEANT, which is why it grew an arm
/// per type and why nothing outside HTTP could reuse a word of it — the same rate-limit was a
/// retryable 429 here and a permanent job failure in the background. The meaning now travels on the
/// exception (<see cref="IFailure"/>) and is read by <see cref="FailureClassifier"/>, which every
/// surface consults. What is left here is the one thing that is genuinely HTTP's business: which
/// status number to write, and what a caller is allowed to be told.</para>
///
/// <para>Nothing is logged here. <c>RequestFailureObserver</c> already recorded it at the severity
/// the failure's kind implies, for every request on every transport; logging again would double every
/// line and disagree about severity on half of them.</para>
/// </summary>
public sealed class GlobalExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        var failure = FailureClassifier.Classify(context.Exception);

        var body = new Dictionary<string, object?> { ["code"] = failure.Code, ["message"] = failure.ClientMessage };

        // Details are what the caller must act on — the provider to link, the scopes to grant. They
        // are merged rather than nested so existing clients keep reading them off the top level.
        if (failure.Details != null)
        {
            foreach (var (key, value) in failure.Details)
            {
                if (value != null && key is not ("code" or "message")) body[key] = value;
            }
        }

        context.Result = new ObjectResult(body) { StatusCode = StatusFor(failure) };

        // Without this MVC rethrows after the filter runs and the response becomes an unshaped 500,
        // discarding everything decided above.
        context.ExceptionHandled = true;
    }

    /// <summary>
    /// One kind, one status. The single conditional is upstream mirroring: what a dependency actually
    /// answered is more useful to the caller than a blanket 502 — a provider's 404 means the repository
    /// is gone, and flattening it would lose that.
    ///
    /// <para>5xx mirrors too, which is arguable — a provider's 500 arriving as our 500 reads to a
    /// caller as our fault. It is preserved here because it is the behaviour that shipped, and a
    /// refactor is the wrong place to change what a status means; the parity tests would have caught
    /// it silently becoming 502, and did.</para>
    /// </summary>
    private static int StatusFor(FailureClassification failure)
    {
        if (failure.Kind == FailureKind.Unavailable && failure.UpstreamStatus is >= 400) return failure.UpstreamStatus.Value;

        return failure.Kind switch
        {
            FailureKind.Invalid => StatusCodes.Status400BadRequest,
            FailureKind.Unauthenticated => StatusCodes.Status401Unauthorized,
            FailureKind.Forbidden => StatusCodes.Status403Forbidden,
            FailureKind.NotFound => StatusCodes.Status404NotFound,
            FailureKind.Conflict => StatusCodes.Status409Conflict,
            FailureKind.Unprocessable => StatusCodes.Status422UnprocessableEntity,
            FailureKind.PreconditionRequired => StatusCodes.Status428PreconditionRequired,
            FailureKind.Exhausted => StatusCodes.Status429TooManyRequests,
            FailureKind.Unavailable => StatusCodes.Status502BadGateway,
            FailureKind.Internal => StatusCodes.Status500InternalServerError,
            _ => StatusCodes.Status500InternalServerError,
        };
    }
}
