using CodeSpace.Core.Services.Learning;
using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Agents;

/// <summary>Thin dispatcher (Rule 16) — the production caller of <see cref="ILessonDistiller.DistillAsync"/>.</summary>
public sealed class DistillLessonsCommandHandler : IRequestHandler<DistillLessonsCommand, DistillLessonsResponse>
{
    private readonly ILessonQualifier _qualifier;
    private readonly ILessonDistiller _distiller;

    public DistillLessonsCommandHandler(ILessonQualifier qualifier, ILessonDistiller distiller)
    {
        _qualifier = qualifier;
        _distiller = distiller;
    }

    public async Task<DistillLessonsResponse> Handle(DistillLessonsCommand request, CancellationToken cancellationToken)
    {
        await _qualifier.QualifyAsync(cancellationToken).ConfigureAwait(false);
        return new DistillLessonsResponse { TeamsDistilled = await _distiller.DistillAsync(cancellationToken).ConfigureAwait(false) };
    }
}
