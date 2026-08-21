using System.ComponentModel.DataAnnotations;
using CodeSpace.Core.Handlers.QueryHandlers.Sessions;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Messages.Dtos.Sessions;
using CodeSpace.Messages.Queries.Sessions;
using Shouldly;

namespace CodeSpace.UnitTests.Handlers.Sessions;

[Trait("Category", "Unit")]
public sealed class PageSessionRunMetadataQueryHandlerTests
{
    [Fact]
    public async Task Handler_scopes_exact_session_selector_to_current_team_and_preserves_page_controls()
    {
        var teamId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var reader = new CapturingReader();
        var query = new PageSessionRunMetadataQuery { SessionId = sessionId, Direction = SessionRunMetadataPageDirection.Older, Cursor = "opaque", Limit = 17 };

        await new PageSessionRunMetadataQueryHandler(reader, new StubCurrentTeam(teamId)).Handle(query, CancellationToken.None);

        reader.Request.ShouldBe(new SessionRunMetadataPageRequest
        {
            TeamId = teamId,
            Selector = new SessionRunMetadataSelector { Kind = SessionRunMetadataSelectorKind.Session, SessionId = sessionId },
            Direction = SessionRunMetadataPageDirection.Older,
            Cursor = "opaque",
            Limit = 17,
        });
    }

    [Fact]
    public async Task Handler_scopes_exact_run_anchor_selector_to_current_team()
    {
        var teamId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var reader = new CapturingReader();

        await new PageSessionRunMetadataQueryHandler(reader, new StubCurrentTeam(teamId))
            .Handle(new PageSessionRunMetadataQuery { RunAnchorId = runId }, CancellationToken.None);

        reader.Request!.Selector.ShouldBe(new SessionRunMetadataSelector { Kind = SessionRunMetadataSelectorKind.RunAnchor, RunAnchorId = runId });
        reader.Request.TeamId.ShouldBe(teamId);
    }

    [Theory]
    [MemberData(nameof(InvalidQueries))]
    public void Invalid_selector_or_range_fails_validation(PageSessionRunMetadataQuery query)
    {
        var errors = new List<ValidationResult>();
        Validator.TryValidateObject(query, new ValidationContext(query), errors, validateAllProperties: true).ShouldBeFalse();
        errors.ShouldNotBeEmpty();
    }

    public static IEnumerable<object[]> InvalidQueries()
    {
        yield return [new PageSessionRunMetadataQuery()];
        yield return [new PageSessionRunMetadataQuery { SessionId = Guid.NewGuid(), RunAnchorId = Guid.NewGuid() }];
        yield return [new PageSessionRunMetadataQuery { SessionId = Guid.NewGuid(), Limit = 0 }];
        yield return [new PageSessionRunMetadataQuery { SessionId = Guid.NewGuid(), Limit = 257 }];
        yield return [new PageSessionRunMetadataQuery { SessionId = Guid.NewGuid(), Direction = SessionRunMetadataPageDirection.Tail, Cursor = "cursor" }];
        yield return [new PageSessionRunMetadataQuery { SessionId = Guid.NewGuid(), Direction = SessionRunMetadataPageDirection.Older }];
        yield return [new PageSessionRunMetadataQuery { SessionId = Guid.NewGuid(), Direction = (SessionRunMetadataPageDirection)99 }];
    }

    private sealed class CapturingReader : ISessionRunMetadataPageReader
    {
        public SessionRunMetadataPageRequest? Request { get; private set; }

        public Task<SessionRunMetadataPage?> ReadAsync(SessionRunMetadataPageRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult<SessionRunMetadataPage?>(null);
        }
    }

    private sealed record StubCurrentTeam(Guid TeamId) : ICurrentTeam
    {
        public Guid? Id => TeamId;
        public bool IsSet => true;
    }
}
