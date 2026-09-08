using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Context.Sources;

/// <summary>Pullable, resumable session-wide view of append-only normalized events across agent runs.</summary>
public sealed class SessionAgentEventsContextSource : IContextSource, IScopedDependency
{
    private readonly ISessionAgentEventReader _reader;

    public SessionAgentEventsContextSource(ISessionAgentEventReader reader) { _reader = reader; }

    public string Kind => "session.events";

    public string Description =>
        "Durable normalized agent events across every run in this work thread, with stable run/sequence ids, event " +
        "kind, bounded text, and safe structured-data references. Optional 'query' filters full event kind/text/run id before paging.";

    public async Task<AgentContextResult> RetrieveAsync(AgentContextQuery query, CancellationToken cancellationToken)
    {
        if (query.SessionId is not { } sessionId) return AgentContextResult.Empty;

        var page = await _reader.ReadAsync(new SessionAgentEventRequest
        {
            TeamId = query.TeamId,
            SessionId = sessionId,
            Query = query.Query,
            Cursor = query.Cursor,
        }, cancellationToken).ConfigureAwait(false);

        if (page.Items.Count == 0) return AgentContextResult.Empty;

        var text = SessionAgentEventText.Render(page);
        return page.NextCursor == null ? AgentContextResult.From(text) : AgentContextResult.Partial(text, page.NextCursor);
    }
}
