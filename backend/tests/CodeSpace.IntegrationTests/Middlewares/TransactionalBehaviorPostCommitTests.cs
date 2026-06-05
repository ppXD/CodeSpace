using Autofac;
using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Mediation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Middlewares;

/// <summary>
/// Pins the post-commit dispatch contract that closes the pre-commit-enqueue race (a worker fetching a
/// Hangfire job before the row it targets is committed → CAS no-op → stuck run). Drives the real
/// <see cref="TransactionalBehavior{TRequest,TResponse}"/> + <see cref="PostCommitActions"/> over a real
/// Postgres transaction; the visibility check uses a SEPARATE connection so "committed" means
/// genuinely committed, not just flushed in the same transaction.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class TransactionalBehaviorPostCommitTests
{
    private readonly PostgresFixture _fixture;

    public TransactionalBehaviorPostCommitTests(PostgresFixture fixture) { _fixture = fixture; }

    private sealed record ProbeCommand : ICommand<Unit>;

    [Fact]
    public async Task Deferred_action_runs_after_commit_and_sees_the_committed_row()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var postCommit = new PostCommitActions(db, NullLogger<PostCommitActions>.Instance);
        var behavior = new TransactionalBehavior<ProbeCommand, Unit>(db, postCommit, NullLogger<TransactionalBehavior<ProbeCommand, Unit>>.Instance);

        var markerId = Guid.NewGuid();
        bool? visibleToFreshConnection = null;

        async Task<Unit> Next(CancellationToken ct)
        {
            // Write a marker INSIDE the behavior's transaction (flush, not commit).
            db.User.Add(new User { Id = markerId, Email = $"pc-{markerId:N}@x", Name = "probe" });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            // A transaction is open → this defers into the post-commit drain.
            await postCommit.RunAfterCommitAsync(async ct2 =>
            {
                using var freshScope = _fixture.BeginScope();
                var freshDb = freshScope.Resolve<CodeSpaceDbContext>();
                visibleToFreshConnection = await freshDb.User.AsNoTracking().AnyAsync(u => u.Id == markerId, ct2).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            return Unit.Value;
        }

        await behavior.Handle(new ProbeCommand(), Next, CancellationToken.None).ConfigureAwait(false);

        // The drain ran the action, and a fresh connection saw the row → the action fired strictly after commit.
        visibleToFreshConnection.ShouldBe(true);
    }

    [Fact]
    public async Task Rolled_back_command_fires_no_post_commit_action_and_persists_nothing()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var postCommit = new PostCommitActions(db, NullLogger<PostCommitActions>.Instance);
        var behavior = new TransactionalBehavior<ProbeCommand, Unit>(db, postCommit, NullLogger<TransactionalBehavior<ProbeCommand, Unit>>.Instance);

        var markerId = Guid.NewGuid();
        var actionRan = false;

        async Task<Unit> Next(CancellationToken ct)
        {
            db.User.Add(new User { Id = markerId, Email = $"pc-{markerId:N}@x", Name = "probe" });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            await postCommit.RunAfterCommitAsync(_ => { actionRan = true; return Task.CompletedTask; }, ct).ConfigureAwait(false);

            throw new InvalidOperationException("handler failed after staging");
        }

        await Should.ThrowAsync<InvalidOperationException>(() => behavior.Handle(new ProbeCommand(), Next, CancellationToken.None)).ConfigureAwait(false);

        actionRan.ShouldBeFalse();

        using var freshScope = _fixture.BeginScope();
        var freshDb = freshScope.Resolve<CodeSpaceDbContext>();
        (await freshDb.User.AsNoTracking().AnyAsync(u => u.Id == markerId).ConfigureAwait(false)).ShouldBeFalse();
    }

    [Fact]
    public async Task RunAfterCommit_runs_inline_when_no_ambient_transaction()
    {
        // The non-command path (a service invoked outside the ICommand pipeline — e.g. an event published
        // directly in a test): no transaction is open, so the action must run immediately, not queue
        // forever waiting for a drain that never comes.
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var postCommit = new PostCommitActions(db, NullLogger<PostCommitActions>.Instance);

        var ran = false;
        await postCommit.RunAfterCommitAsync(_ => { ran = true; return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false);

        ran.ShouldBeTrue();
    }

    [Fact]
    public async Task A_failing_post_commit_action_neither_blocks_siblings_nor_fails_the_command()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var postCommit = new PostCommitActions(db, NullLogger<PostCommitActions>.Instance);
        var behavior = new TransactionalBehavior<ProbeCommand, Unit>(db, postCommit, NullLogger<TransactionalBehavior<ProbeCommand, Unit>>.Instance);

        var ranFirst = false;
        var ranThird = false;

        async Task<Unit> Next(CancellationToken ct)
        {
            await postCommit.RunAfterCommitAsync(_ => { ranFirst = true; return Task.CompletedTask; }, ct).ConfigureAwait(false);
            await postCommit.RunAfterCommitAsync(_ => throw new InvalidOperationException("dispatch 2 failed"), ct).ConfigureAwait(false);
            await postCommit.RunAfterCommitAsync(_ => { ranThird = true; return Task.CompletedTask; }, ct).ConfigureAwait(false);
            return Unit.Value;
        }

        // The command must succeed even though one post-commit action throws (it's logged + skipped).
        await behavior.Handle(new ProbeCommand(), Next, CancellationToken.None).ConfigureAwait(false);

        ranFirst.ShouldBeTrue();
        ranThird.ShouldBeTrue();
    }
}
