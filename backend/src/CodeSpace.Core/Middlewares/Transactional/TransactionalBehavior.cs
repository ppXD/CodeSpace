using CodeSpace.Messages.Mediation;
using CodeSpace.Core.Persistence.Db;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Middlewares.Transactional;

public sealed class TransactionalBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : ICommand<TResponse>
{
    private readonly CodeSpaceDbContext _dbContext;
    private readonly IPostCommitActions _postCommit;
    private readonly ILogger<TransactionalBehavior<TRequest, TResponse>> _logger;

    public TransactionalBehavior(CodeSpaceDbContext dbContext, IPostCommitActions postCommit, ILogger<TransactionalBehavior<TRequest, TResponse>> logger)
    {
        _dbContext = dbContext;
        _postCommit = postCommit;
        _logger = logger;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        // Nested command — the outer behavior owns the transaction AND the post-commit drain, so just pass through.
        // Checked BEFORE the marker so a sweep dispatched inside another command can never drain that command's
        // deferred actions before it commits.
        if (_dbContext.Database.CurrentTransaction != null) return await next(cancellationToken).ConfigureAwait(false);

        if (request is INonTransactionalCommand) return await HandleWithoutTransactionAsync(next, cancellationToken).ConfigureAwait(false);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        TResponse response;
        try
        {
            response = await next(cancellationToken).ConfigureAwait(false);

            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command {CommandType} failed; transaction rolled back", typeof(TRequest).Name);
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        // Only after the row is durably committed: fire deferred side effects (e.g. enqueue the
        // background job that executes the run just staged). Never inside the transaction — a worker
        // could otherwise fetch the job before the row is visible. Drains on the success path only;
        // a rolled-back command fires nothing.
        await _postCommit.RunAllAsync(cancellationToken).ConfigureAwait(false);

        return response;
    }

    /// <summary>
    /// A <see cref="INonTransactionalCommand"/> sweep: no transaction, and no framework <c>SaveChanges</c> — its
    /// per-row CAS writes commit themselves. The drain still runs, because a step that defers behind a
    /// SERVICE-owned transaction queues its action and would otherwise have no drain site at all.
    /// </summary>
    private async Task<TResponse> HandleWithoutTransactionAsync(RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var response = await next(cancellationToken).ConfigureAwait(false);

        await _postCommit.RunAllAsync(cancellationToken).ConfigureAwait(false);

        return response;
    }
}
