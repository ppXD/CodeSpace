using CodeSpace.Messages.Mediation;
using CodeSpace.Core.Persistence.Db;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Middlewares.Transactional;

public sealed class TransactionalBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : ICommand<TResponse>
{
    private readonly CodeSpaceDbContext _dbContext;
    private readonly ILogger<TransactionalBehavior<TRequest, TResponse>> _logger;

    public TransactionalBehavior(CodeSpaceDbContext dbContext, ILogger<TransactionalBehavior<TRequest, TResponse>> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (_dbContext.Database.CurrentTransaction != null) return await next(cancellationToken).ConfigureAwait(false);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var response = await next(cancellationToken).ConfigureAwait(false);

            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command {CommandType} failed; transaction rolled back", typeof(TRequest).Name);
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}
