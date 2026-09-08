using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Context.Sources;

/// <summary>
/// Pullable, resumable view of governed side-effect receipts across a work thread. This gives a cold model stable
/// identities and observed outcomes without parsing prior prose or promising cross-attempt exactly-once.
/// </summary>
public sealed class SessionEffectsContextSource : IContextSource, IScopedDependency
{
    private readonly ISessionEffectReceiptReader _reader;

    public SessionEffectsContextSource(ISessionEffectReceiptReader reader) { _reader = reader; }

    public string Kind => "session.effects";

    public string Description =>
        "Durable receipts for governed side-effecting tool calls across this work thread, with stable ledger/run ids, " +
        "input hash, status, and bounded recorded result/error text. Excludes decision.request control traffic. " +
        "Optional 'query' filters named receipt fields before paging.";

    public async Task<AgentContextResult> RetrieveAsync(AgentContextQuery query, CancellationToken cancellationToken)
    {
        if (query.SessionId is not { } sessionId) return AgentContextResult.Empty;

        var page = await _reader.ReadAsync(new SessionEffectReceiptRequest
        {
            TeamId = query.TeamId,
            SessionId = sessionId,
            Query = query.Query,
            Cursor = query.Cursor,
        }, cancellationToken).ConfigureAwait(false);

        if (page.Items.Count == 0) return AgentContextResult.Empty;

        var text = SessionEffectReceiptText.Render(page, launchDigest: false);
        return page.NextCursor == null ? AgentContextResult.From(text) : AgentContextResult.Partial(text, page.NextCursor);
    }
}
