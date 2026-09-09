using CodeSpace.Core.Handlers.CommandHandlers.Agents;
using CodeSpace.Core.Services.Learning;
using CodeSpace.Messages.Commands.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Handlers.Agents;

[Trait("Category", "Unit")]
public sealed class DistillLessonsCommandHandlerTests
{
    [Fact]
    public async Task Nightly_lifecycle_qualifies_settled_exposures_before_minting_new_candidates()
    {
        var calls = new List<string>();
        var handler = new DistillLessonsCommandHandler(new RecordingQualifier(calls), new RecordingDistiller(calls));

        var result = await handler.Handle(new DistillLessonsCommand(), CancellationToken.None);

        calls.ShouldBe(["qualify", "distill"]);
        result.TeamsDistilled.ShouldBe(3);
    }

    private sealed class RecordingQualifier(List<string> calls) : ILessonQualifier
    {
        public Task<int> QualifyAsync(CancellationToken cancellationToken)
        {
            calls.Add("qualify");
            return Task.FromResult(2);
        }
    }

    private sealed class RecordingDistiller(List<string> calls) : ILessonDistiller
    {
        public Task<int> DistillAsync(CancellationToken cancellationToken)
        {
            calls.Add("distill");
            return Task.FromResult(3);
        }

        public Task DistillTeamAsync(Guid teamId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
