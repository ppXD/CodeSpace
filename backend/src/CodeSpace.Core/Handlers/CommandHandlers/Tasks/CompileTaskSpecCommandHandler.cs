using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Tasks.SpecPreview;
using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Tasks;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Tasks;

/// <summary>Thin dispatcher (Rule 16): the team from <see cref="ICurrentTeam"/> (never the body), one service call.</summary>
public sealed class CompileTaskSpecCommandHandler : IRequestHandler<CompileTaskSpecCommand, CompileTaskSpecResult>
{
    private readonly ITaskSpecCompiler _compiler;
    private readonly ICurrentTeam _currentTeam;

    public CompileTaskSpecCommandHandler(ITaskSpecCompiler compiler, ICurrentTeam currentTeam)
    {
        _compiler = compiler;
        _currentTeam = currentTeam;
    }

    public Task<CompileTaskSpecResult> Handle(CompileTaskSpecCommand request, CancellationToken cancellationToken) =>
        _compiler.CompileAsync(_currentTeam.Id!.Value, request.Goal, request.RepositoryId, cancellationToken);
}
