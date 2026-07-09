using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Context.Sources;

/// <summary>
/// The <c>session.summary</c> source — the thread's rolling LLM-distilled summary of OLDER turns (those scrolled out of
/// the digest's recent window), maintained by <c>SessionSummarizer</c> on <c>WorkSession.Summary</c>. The launch digest
/// already prepends this summary, but an agent that has since spent its window on other work can pull it back here. A
/// thread that never grew past the recent window has no summary yet → a clean miss. Team- + session-scoped; reads the
/// committed value (AsNoTracking) — a follow-up turn's agent runs after the prior turn's summarize/commit.
/// </summary>
public sealed class SessionSummaryContextSource : IContextSource, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public SessionSummaryContextSource(CodeSpaceDbContext db)
    {
        _db = db;
    }

    public string Kind => "session.summary";

    public string Description =>
        "The rolling distilled summary of this work thread's earlier turns (those scrolled out of the recent window). " +
        "Returns nothing when the thread is short enough that no summary exists yet.";

    public async Task<AgentContextResult> RetrieveAsync(AgentContextQuery query, CancellationToken cancellationToken)
    {
        if (query.SessionId is not { } sessionId) return AgentContextResult.Empty;

        var row = await _db.WorkSession.AsNoTracking()
            .Where(s => s.Id == sessionId && s.TeamId == query.TeamId)
            .Select(s => new { s.Summary, s.SummaryStaleSinceTurn })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (row is null || string.IsNullOrWhiteSpace(row.Summary)) return AgentContextResult.Empty;

        var text = $"# Distilled summary of earlier work in this thread\n{row.Summary.Trim()}";

        if (row.SummaryStaleSinceTurn is { } staleSince)
            text += $"\n\n(Note: turns from {staleSince} onward have not yet been folded into this summary — it may be incomplete.)";

        return AgentContextResult.From(text);
    }
}
