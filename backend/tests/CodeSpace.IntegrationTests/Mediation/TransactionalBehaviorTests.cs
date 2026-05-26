using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.IntegrationTests.Infrastructure;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Mediation;

[Collection(PostgresCollection.Name)]
public class TransactionalBehaviorTests
{
    private readonly PostgresFixture _fixture;

    public TransactionalBehaviorTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Command_commits_on_success()
    {
        var email = UniqueEmail("tx-ok");

        using (var writeScope = _fixture.BeginScope())
        {
            var mediator = writeScope.Resolve<IMediator>();
            await mediator.Send(new CreateTestUserCommand(email, "OK")).ConfigureAwait(false);
        }

        (await ExistsAsync(email).ConfigureAwait(false)).ShouldBeTrue("entity should exist after successful command");
    }

    [Fact]
    public async Task Command_rolls_back_on_throw()
    {
        var email = UniqueEmail("tx-rollback");

        using (var writeScope = _fixture.BeginScope())
        {
            var mediator = writeScope.Resolve<IMediator>();
            var act = async () => await mediator.Send(new CreateUserThenThrowCommand(email, "Fail")).ConfigureAwait(false);
            await act.ShouldThrowAsync<InvalidOperationException>().ConfigureAwait(false);
        }

        (await ExistsAsync(email).ConfigureAwait(false)).ShouldBeFalse("entity must not exist after rolled-back command");
    }

    [Fact]
    public async Task Nested_command_participates_in_outer_transaction_and_both_commit()
    {
        var email = UniqueEmail("tx-nested");

        Guid outerId;
        Guid innerId;
        using (var writeScope = _fixture.BeginScope())
        {
            var mediator = writeScope.Resolve<IMediator>();
            (outerId, innerId) = await mediator.Send(new NestedCreateCommand(email, "Nested")).ConfigureAwait(false);
        }

        using var verifyScope = _fixture.BeginScope();
        var db = verifyScope.Resolve<CodeSpaceDbContext>();

        (await db.User.AsNoTracking().AnyAsync(u => u.Id == outerId).ConfigureAwait(false)).ShouldBeTrue("outer entity should be committed");
        (await db.User.AsNoTracking().AnyAsync(u => u.Id == innerId).ConfigureAwait(false)).ShouldBeTrue("inner entity should be committed within outer transaction");
    }

    [Fact]
    public async Task Nested_inner_rolls_back_when_outer_throws_after_inner_succeeded()
    {
        var email = UniqueEmail("tx-nested-throw");

        using (var writeScope = _fixture.BeginScope())
        {
            var mediator = writeScope.Resolve<IMediator>();
            var act = async () => await mediator.Send(new NestedThrowCommand(email, "Throw")).ConfigureAwait(false);
            await act.ShouldThrowAsync<InvalidOperationException>().ConfigureAwait(false);
        }

        using var verifyScope = _fixture.BeginScope();
        var db = verifyScope.Resolve<CodeSpaceDbContext>();

        var anyInner = await db.User.AsNoTracking().AnyAsync(u => u.Email.StartsWith($"{email}.inner")).ConfigureAwait(false);
        anyInner.ShouldBeFalse("inner command's writes must roll back when outer command throws");
    }

    private async Task<bool> ExistsAsync(string email)
    {
        using var verifyScope = _fixture.BeginScope();
        var db = verifyScope.Resolve<CodeSpaceDbContext>();
        return await db.User.AsNoTracking().AnyAsync(u => u.Email == email).ConfigureAwait(false);
    }

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test";
}
