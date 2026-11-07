using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Launch;

/// <summary>Default <see cref="ILaunchGroundingResolver"/> — the thread's rolling summary brought up to date, then its prior-turn digest composed over the seed's own grounding.</summary>
public sealed class LaunchGroundingResolver : ILaunchGroundingResolver, IScopedDependency
{
    private readonly ISessionContextBuilder _sessionContext;
    private readonly ISessionSummarizer _sessionSummarizer;

    public LaunchGroundingResolver(ISessionContextBuilder sessionContext, ISessionSummarizer sessionSummarizer)
    {
        _sessionContext = sessionContext;
        _sessionSummarizer = sessionSummarizer;
    }

    public async Task<string?> ResolveAsync(TaskLaunchRequest request, TaskLaunchSeed seed, CancellationToken cancellationToken)
    {
        if (request.ContinueSessionId is not { } sessionId) return seed.GroundingContext;

        // Fold any turns that scrolled out of the recent window into the thread's rolling summary BEFORE building the
        // digest, so a long thread's early context is preserved. Best-effort + fail-open (no model / error leaves it).
        await _sessionSummarizer.EnsureSummaryUpToDateAsync(sessionId, request.TeamId, cancellationToken).ConfigureAwait(false);

        var priorTurns = await _sessionContext.BuildAsync(sessionId, request.TeamId, cancellationToken).ConfigureAwait(false);

        return ComposeGrounding(priorTurns, seed.GroundingContext);
    }

    /// <summary>Join the prior-turn digest and the seed's own grounding (either may be absent) into one block, digest first.</summary>
    private static string? ComposeGrounding(string? priorTurns, string? seedGrounding)
    {
        if (string.IsNullOrWhiteSpace(priorTurns)) return seedGrounding;
        if (string.IsNullOrWhiteSpace(seedGrounding)) return priorTurns;

        return $"{priorTurns}\n\n{seedGrounding}";
    }
}
