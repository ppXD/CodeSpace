using System.Diagnostics;
using CodeSpace.Core.Services.Identity;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Middlewares.Logging;

public sealed class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly ILogger<LoggingBehavior<TRequest, TResponse>> _logger;
    private readonly ICurrentUser _currentUser;

    public LoggingBehavior(ILogger<LoggingBehavior<TRequest, TResponse>> logger, ICurrentUser currentUser)
    {
        _logger = logger;
        _currentUser = currentUser;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;

        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["RequestName"] = requestName,
            ["UserId"] = _currentUser?.Id?.ToString() ?? "system"
        });

        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Mediator begin {RequestName}", requestName);

        try
        {
            var response = await next(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Mediator end {RequestName} ok in {ElapsedMs}ms", requestName, stopwatch.ElapsedMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mediator end {RequestName} failed in {ElapsedMs}ms", requestName, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
