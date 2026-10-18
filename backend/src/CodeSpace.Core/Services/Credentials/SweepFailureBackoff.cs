using System.Collections.Concurrent;
using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// What a recurring sweep remembers about the candidates it could not handle. A candidate that fails stays a candidate —
/// the next tick may succeed — but one that has failed several ticks running is not going to heal in the next five minutes,
/// and attempting it again every tick forever costs a burst of queries and a warning each time. After
/// <see cref="SweepFailureBackoff.FailuresBeforeBackoff"/> failures in a row it is set aside for a window and not attempted;
/// when the window ends it is attempted once, and a failure sets it aside for another. A success forgets it.
///
/// <para>Only an optimisation of the sweep, never its correctness: the sweep's fairness comes from walking its backlog in
/// pages, so a set-aside candidate costs the sweep nothing it needs. The memory is the process's own — a restart, or another
/// worker, simply attempts the candidate again.</para>
/// </summary>
public interface ISweepFailureBackoff
{
    /// <summary>True while <paramref name="candidateId"/> is set aside from <paramref name="sweep"/>.</summary>
    bool IsBackedOff(string sweep, Guid candidateId);

    /// <summary>Remember one more failure in a row. Returns true when it set the candidate aside, so the caller can say so once.</summary>
    bool RecordFailure(string sweep, Guid candidateId);

    /// <summary>Forget the candidate's failures.</summary>
    void RecordSuccess(string sweep, Guid candidateId);
}

public sealed class SweepFailureBackoff : ISweepFailureBackoff, ISingletonDependency
{
    /// <summary>Failures in a row after which a candidate is set aside.</summary>
    public const int FailuresBeforeBackoff = 3;

    /// <summary>How long a candidate stays set aside — about twelve ticks of a five-minute sweep.</summary>
    public static readonly TimeSpan BackoffWindow = TimeSpan.FromHours(1);

    /// <summary>A failure this old counts for nothing: the candidate went quiet, and is judged afresh.</summary>
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(24);

    private const int PruneAbove = 1024;

    private readonly ConcurrentDictionary<(string Sweep, Guid CandidateId), Standing> _standing = new();
    private readonly TimeProvider _clock;

    public SweepFailureBackoff(TimeProvider clock) { _clock = clock; }

    public bool IsBackedOff(string sweep, Guid candidateId)
    {
        if (!_standing.TryGetValue((sweep, candidateId), out var standing)) return false;

        return standing.Failures >= FailuresBeforeBackoff && _clock.GetUtcNow() - standing.LastFailure < BackoffWindow;
    }

    public bool RecordFailure(string sweep, Guid candidateId)
    {
        var now = _clock.GetUtcNow();

        var standing = _standing.AddOrUpdate((sweep, candidateId), _ => new Standing(1, now), (_, current) => Next(current, now));

        if (_standing.Count > PruneAbove) PruneForgotten(now);

        return standing.Failures >= FailuresBeforeBackoff;
    }

    public void RecordSuccess(string sweep, Guid candidateId) => _standing.TryRemove((sweep, candidateId), out _);

    private static Standing Next(Standing current, DateTimeOffset now) =>
        now - current.LastFailure > ForgetAfter ? new Standing(1, now) : new Standing(current.Failures + 1, now);

    /// <summary>Candidates fixed some other way never succeed through the sweep, so nothing else would ever forget them.</summary>
    private void PruneForgotten(DateTimeOffset now)
    {
        foreach (var (key, standing) in _standing)
            if (now - standing.LastFailure > ForgetAfter) _standing.TryRemove(key, out _);
    }

    private readonly record struct Standing(int Failures, DateTimeOffset LastFailure);
}
