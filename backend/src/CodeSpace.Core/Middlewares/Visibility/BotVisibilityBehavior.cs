using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Visibility;
using MediatR;

namespace CodeSpace.Core.Middlewares.Visibility;

/// <summary>
/// Flips the request-scoped <see cref="IBotVisibility"/> ON for requests that implement
/// <see cref="IBotInclusive"/>, BEFORE the handler runs — so the EF Core global query filter on
/// <c>User</c> lets bot users through for that request only. Every other request runs with the
/// default (bots excluded), which is what makes the exclusion impossible to forget: a query is
/// bot-free unless its request type explicitly opts in.
/// </summary>
public sealed class BotVisibilityBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly IBotVisibility _botVisibility;

    public BotVisibilityBehavior(IBotVisibility botVisibility)
    {
        _botVisibility = botVisibility;
    }

    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is IBotInclusive) _botVisibility.IncludeBots = true;

        return next(cancellationToken);
    }
}
