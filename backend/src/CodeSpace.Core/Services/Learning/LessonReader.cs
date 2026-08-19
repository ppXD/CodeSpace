using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace CodeSpace.Core.Services.Learning;

public interface ILessonReader
{
    /// <summary>The team's CURRENT lessons for injection — repo-matched lessons first (the sharper key), then the freshest; capped at <paramref name="take"/>.</summary>
    Task<IReadOnlyList<Lesson>> ListCurrentAsync(Guid teamId, Guid? repositoryId, int take, CancellationToken cancellationToken);
}

/// <summary>Arc D / D2 — the learning loop's read side: the injection reader over the lesson ledger.</summary>
public sealed class LessonReader : ILessonReader, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public LessonReader(CodeSpaceDbContext db) => _db = db;

    public async Task<IReadOnlyList<Lesson>> ListCurrentAsync(Guid teamId, Guid? repositoryId, int take, CancellationToken cancellationToken) =>
        await _db.Lesson.AsNoTracking()
            .Where(l => l.TeamId == teamId && l.InvalidatedAt == null)
            .OrderByDescending(l => repositoryId != null && l.RepositoryId == repositoryId)
            .ThenByDescending(l => l.ValidFrom)
            .Take(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// D2's A/B assignment — deterministic, toggle-free: the arm is a pure hash of (team, task text), recorded on the
/// plan itself so the north-star referee can slice injected vs withheld runs afterwards. No randomness (a retry
/// of the same task plans under the same arm), no env switch (retiring the experiment is a one-line reviewed
/// edit here).
/// </summary>
public static class LessonArms
{
    public const string Injected = "injected";
    public const string Withheld = "withheld";
    /// <summary>No current lesson existed to inject — outside the experiment entirely (never counted as a control).</summary>
    public const string None = "none";

    public static string Assign(Guid teamId, string taskText)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(teamId + "\n" + taskText));
        return (hash[0] & 1) == 0 ? Injected : Withheld;
    }
}
