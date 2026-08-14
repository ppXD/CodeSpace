using CodeSpace.Api.Filters;
using CodeSpace.Core.Failures;
using CodeSpace.Core.Middlewares.Failures;
using CodeSpace.Core.Services.Identity;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Filters;

/// <summary>
/// Every failure a caller is told about must exist in a log somewhere. Two surfaces can record one —
/// <c>RequestFailureObserver</c> for anything escaping a MediatR handler, and the MVC filter for
/// everything else — and the contract is exactly-once: never zero, never twice.
///
/// <para>Zero was the real state before this. The filter deliberately logged nothing, on the reasoning
/// that the observer had already done it; but the observer only ever sees exceptions that escape a
/// HANDLER. Anything thrown outside the pipeline — a controller around its <c>Send</c>, model binding,
/// an auth filter — produced a masked 500 and no line on any sink, so an operator searching for the
/// error found an empty result identical to nothing having gone wrong.</para>
/// </summary>
[Trait("Category", "Integration")]
public class FailuresAreAlwaysLoggedTests
{
    [Fact]
    public void A_failure_the_pipeline_never_saw_is_recorded_by_the_filter()
    {
        var logger = new CapturingLogger();

        Run(new InvalidOperationException("thrown in a controller, outside Send"), logger);

        logger.Entries.Count.ShouldBe(1,
            customMessage: "An exception thrown outside the MediatR pipeline reaches no other recorder. Without this " +
                           "line the caller gets a 500 and nothing exists in any sink to explain it.");
    }

    /// <summary>
    /// Drives the REAL observer rather than stamping the mark by hand. Stamping it here would only
    /// prove the filter honours a mark, and would stay green if the observer never set one — which is
    /// the half that makes exactly-once hold.
    /// </summary>
    [Fact]
    public async Task A_failure_the_observer_recorded_is_not_recorded_again_by_the_filter()
    {
        var exception = new InvalidOperationException("escaped a MediatR handler");

        var observer = new RequestFailureObserver<string, InvalidOperationException>(
            NullLogger<RequestFailureObserver<string, InvalidOperationException>>.Instance,
            new TestCurrentUser(Guid.NewGuid()),
            new StubCurrentTeam(Guid.NewGuid()));

        await observer.Execute("a-request", exception, CancellationToken.None);

        var logger = new CapturingLogger();
        Run(exception, logger);

        logger.Entries.ShouldBeEmpty(
            customMessage: "The observer already logged this one; a second line would double every pipeline failure. " +
                           "If this fails, the observer stopped marking and every pipeline failure is now logged twice.");
    }

    /// <summary>The other direction: an exception the observer never saw carries no mark, so the filter must record it.</summary>
    [Fact]
    public void An_exception_the_observer_never_saw_carries_no_mark()
    {
        FailureLogging.WasLogged(new InvalidOperationException("never reached a handler")).ShouldBeFalse();
    }

    /// <summary>
    /// The severity comes from what the failure MEANS, so an unlogged refusal does not arrive as an
    /// error just because it happened to be caught here instead of in the pipeline.
    /// </summary>
    [Theory]
    [InlineData(LogLevel.Error)]
    public void An_internal_failure_is_recorded_at_error(LogLevel expected)
    {
        var logger = new CapturingLogger();

        Run(new Exception("an invariant broke"), logger);

        logger.Entries.Single().Level.ShouldBe(expected);
    }

    [Fact]
    public void A_refusal_is_recorded_below_error()
    {
        var logger = new CapturingLogger();

        Run(new PasswordRotationRequiredException(), logger);

        logger.Entries.Single().Level.ShouldBe(LogLevel.Information,
            customMessage: "A refusal is the system working. Recording it as an error is how the real errors get buried.");
    }

    /// <summary>A caller who left is not a failure, and the observer skips it for the same reason.</summary>
    [Fact]
    public void A_cancelled_request_is_not_recorded()
    {
        var logger = new CapturingLogger();

        Run(new OperationCanceledException(), logger);

        logger.Entries.ShouldBeEmpty();
    }

    private static void Run(Exception exception, CapturingLogger logger)
    {
        var filter = new GlobalExceptionFilter(logger);
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        var context = new ExceptionContext(actionContext, new List<IFilterMetadata>()) { Exception = exception };

        filter.OnException(context);
    }

    private sealed class StubCurrentTeam : ICurrentTeam
    {
        public StubCurrentTeam(Guid? id) { Id = id; }

        public Guid? Id { get; }
        public bool IsSet => Id is not null;
    }

    private sealed class CapturingLogger : ILogger<GlobalExceptionFilter>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
