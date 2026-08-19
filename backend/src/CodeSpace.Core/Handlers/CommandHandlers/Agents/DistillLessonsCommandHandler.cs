using CodeSpace.Core.Services.Learning;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

/// <summary>Thin dispatcher (Rule 16) — the production caller of <see cref="ILessonDistiller.DistillAsync"/>.</summary>
public sealed class DistillLessonsCommandHandler : IRequestHandler<DistillLessonsCommand, DistillLessonsResponse>
{
    private readonly ILessonDistiller _distiller;

    public DistillLessonsCommandHandler(ILessonDistiller distiller)
    {
        _distiller = distiller;
    }

    public async Task<DistillLessonsResponse> Handle(DistillLessonsCommand request, CancellationToken cancellationToken)
    {
        return new DistillLessonsResponse { TeamsDistilled = await _distiller.DistillAsync(cancellationToken).ConfigureAwait(false) };
    }
}
