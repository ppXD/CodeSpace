using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Context.Sources;

/// <summary>
/// Pullable, resumable view of the calling agent run's own append-only normalized events. They carry its raw tool output,
/// so another run of the thread — whose tier and repositories may reach what this one may not — is read through its turn
/// summary (<c>session.turns</c>), not its events.
/// </summary>
public sealed class SessionAgentEventsContextSource : IContextSource, IScopedDependency
{
    private readonly ISessionAgentEventReader _reader;

    public SessionAgentEventsContextSource(ISessionAgentEventReader reader) { _reader = reader; }

    public string Kind => "session.events";

    public string Description =>
        "Durable normalized events of this agent run, with stable run/sequence ids, event kind, bounded text, and safe " +
        "structured-data references. Other runs of this work thread are read through session.turns. Optional 'query' " +
        "filters the event kind, run id and the bounded text shown before paging.";

    public async Task<AgentContextResult> RetrieveAsync(AgentContextQuery query, CancellationToken cancellationToken)
    {
        if (query.SessionId is not { } sessionId) return AgentContextResult.Empty;

        var page = await _reader.ReadAsync(new SessionAgentEventRequest
        {
            TeamId = query.TeamId,
            SessionId = sessionId,
            AgentRunId = query.RunId,
            Query = query.Query,
            Cursor = query.Cursor,
        }, cancellationToken).ConfigureAwait(false);

        if (page.Items.Count == 0) return AgentContextResult.Empty;

        var text = SessionAgentEventText.Render(page);
        return page.NextCursor == null ? AgentContextResult.From(text) : AgentContextResult.Partial(text, page.NextCursor);
    }
}
