using CodeSpace.Messages.Mediation;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using MediatR;

namespace CodeSpace.IntegrationTests.Infrastructure;

// Simple write command — succeeds.
public record CreateTestUserCommand(string Email, string Name) : ICommand<Guid>;

public class CreateTestUserHandler : IRequestHandler<CreateTestUserCommand, Guid>
{
    private readonly CodeSpaceDbContext _db;

    public CreateTestUserHandler(CodeSpaceDbContext db) { _db = db; }

    public Task<Guid> Handle(CreateTestUserCommand request, CancellationToken cancellationToken)
    {
        var user = new User { Id = Guid.NewGuid(), Email = request.Email, Name = request.Name };
        _db.User.Add(user);
        return Task.FromResult(user.Id);
    }
}


// Write command that adds an entity then throws — used to test rollback.
public record CreateUserThenThrowCommand(string Email, string Name) : ICommand<Guid>;

public class CreateUserThenThrowHandler : IRequestHandler<CreateUserThenThrowCommand, Guid>
{
    private readonly CodeSpaceDbContext _db;

    public CreateUserThenThrowHandler(CodeSpaceDbContext db) { _db = db; }

    public Task<Guid> Handle(CreateUserThenThrowCommand request, CancellationToken cancellationToken)
    {
        var user = new User { Id = Guid.NewGuid(), Email = request.Email, Name = request.Name };
        _db.User.Add(user);
        throw new InvalidOperationException("Intentional failure for rollback test");
    }
}


// Nested command: outer adds an entity, then dispatches inner command via IMediator.
// Inner must participate in outer's transaction (TransactionalBehavior nested-awareness).
public record NestedCreateCommand(string Email, string Name) : ICommand<(Guid Outer, Guid Inner)>;

public class NestedCreateHandler : IRequestHandler<NestedCreateCommand, (Guid Outer, Guid Inner)>
{
    private readonly IMediator _mediator;
    private readonly CodeSpaceDbContext _db;

    public NestedCreateHandler(IMediator mediator, CodeSpaceDbContext db) { _mediator = mediator; _db = db; }

    public async Task<(Guid Outer, Guid Inner)> Handle(NestedCreateCommand request, CancellationToken cancellationToken)
    {
        var outerUser = new User { Id = Guid.NewGuid(), Email = $"{request.Email}.outer", Name = $"{request.Name} outer" };
        _db.User.Add(outerUser);

        var innerId = await _mediator.Send(new CreateTestUserCommand($"{request.Email}.inner", $"{request.Name} inner"), cancellationToken).ConfigureAwait(false);

        return (outerUser.Id, innerId);
    }
}


// Nested command that throws AFTER inner succeeded — inner must also rollback.
public record NestedThrowCommand(string Email, string Name) : ICommand<Unit>;

public class NestedThrowHandler : IRequestHandler<NestedThrowCommand, Unit>
{
    private readonly IMediator _mediator;

    public NestedThrowHandler(IMediator mediator) { _mediator = mediator; }

    public async Task<Unit> Handle(NestedThrowCommand request, CancellationToken cancellationToken)
    {
        await _mediator.Send(new CreateTestUserCommand($"{request.Email}.inner", $"{request.Name} inner"), cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("Outer fails after inner succeeded");
    }
}
