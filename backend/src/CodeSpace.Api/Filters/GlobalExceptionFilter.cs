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
/// <para>It also records anything nobody else did. <c>RequestFailureObserver</c> covers every
/// exception that escapes a MediatR HANDLER and marks it, so this stays quiet for those rather than
/// doubling the line. What it does not cover is everything thrown OUTSIDE the pipeline — a controller
/// around its <c>Send</c>, model binding, an auth filter, a middleware — and that gap was silent: the
/// caller got a masked 500 and no log line existed anywhere, on any sink, so the failure was
/// indistinguishable from one that never happened.</para>
/// </summary>
public sealed class GlobalExceptionFilter : IExceptionFilter
{
    private readonly ILogger<GlobalExceptionFilter> _logger;

    public GlobalExceptionFilter(ILogger<GlobalExceptionFilter> logger) { _logger = logger; }

    public void OnException(ExceptionContext context)
    {
        var failure = FailureClassifier.Classify(context.Exception);

        LogIfNobodyElseDid(context, failure);

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
    /// Record the failure unless the MediatR observer already did. Same severity table, so a failure
    /// does not change how loud it is depending on which layer happened to catch it.
    /// </summary>
    private void LogIfNobodyElseDid(ExceptionContext context, FailureClassification failure)
    {
        // The caller leaving is not a failure, and the observer skips it for the same reason.
        if (context.Exception is OperationCanceledException) return;

        if (FailureLogging.WasLogged(context.Exception)) return;

        _logger.Log(
            FailureLogging.SeverityFor(failure.Kind),
            context.Exception,
            "{Method} {Path} failed outside the request pipeline: {FailureKind}/{FailureCode}",
            context.HttpContext.Request.Method,
            context.HttpContext.Request.Path.Value,
            failure.Kind,
            failure.Code);
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
