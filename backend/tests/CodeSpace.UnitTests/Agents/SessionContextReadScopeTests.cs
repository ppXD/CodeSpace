using System.Text.RegularExpressions;
using CodeSpace.Core.Services.Agents.Context.Sources;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// What <c>get_context</c>'s session.events and session.effects let a run read. Raw event text — file contents, command
/// output — is read back only from the calling run's own events; another run of the thread is read through its turn
/// summary (session.turns), whatever its posture or repositories were. A free-text query is matched only against the
/// clipped text a reader shows, so found / not found cannot spell out what lies past the clip.
/// </summary>
[Trait("Category", "Unit")]
public class SessionContextReadScopeTests
{
    [Fact]
    public async Task Session_events_are_read_for_the_calling_run_only()
    {
        var reader = new CapturingEventReader();
        var query = new AgentContextQuery { TeamId = Guid.NewGuid(), RunId = Guid.NewGuid(), SessionId = Guid.NewGuid(), Query = "needle", Cursor = "c" };

        await new SessionAgentEventsContextSource(reader).RetrieveAsync(query, CancellationToken.None);

        var request = reader.Requests.ShouldHaveSingleItem();
        request.AgentRunId.ShouldBe(query.RunId, "the reader is asked for the caller's own events, never the thread's");
        request.TeamId.ShouldBe(query.TeamId);
        request.SessionId.ShouldBe(query.SessionId!.Value);
        request.Query.ShouldBe("needle");
        request.Cursor.ShouldBe("c");
    }

    [Fact]
    public void Session_events_say_whose_events_they_are_and_where_the_rest_of_the_thread_is()
    {
        var description = new SessionAgentEventsContextSource(new CapturingEventReader()).Description;

        description.ShouldContain("this agent run");
        description.ShouldContain("session.turns");
    }

    [Theory]
    [InlineData("session.events")]
    [InlineData("session.effects")]
    public void A_free_text_query_matches_only_the_clipped_text_the_reader_shows(string source)
    {
        var sql = source == "session.events" ? SessionAgentEventReader.ListSql : SessionEffectReceiptReader.ListSql;
        var freeText = new[] { "event.text", "result_jsonb", "ledger.error" };

        var matched = Regex.Matches(sql, @"OR\s+(?<subject>.+?)\s+ILIKE").Select(match => match.Groups["subject"].Value).ToList();

        matched.ShouldNotBeEmpty();
        foreach (var subject in matched.Where(subject => freeText.Any(subject.Contains)))
            subject.ShouldContain(", @excerpt_characters)", customMessage: $"'{subject}' is matched past the clip the reader shows — found / not found would read what the caller is never shown");
    }

    private sealed class CapturingEventReader : ISessionAgentEventReader
    {
        public List<SessionAgentEventRequest> Requests { get; } = new();

        public Task<SessionAgentEventPage> ReadAsync(SessionAgentEventRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new SessionAgentEventPage { Items = [] });
        }
    }
}
