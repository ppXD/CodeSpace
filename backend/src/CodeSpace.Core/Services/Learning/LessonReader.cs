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
/// D2's A/B assignment — deterministic, toggle-free: the arm is a pure hash of (team, TASK GOAL), recorded on the
/// authored plan (planner lane) and on every decision row of the run (supervisor lane) so the north-star referee
/// can slice injected vs withheld runs afterwards. No randomness (a retry of the same task plans under the same
/// arm), no env switch (retiring the experiment is a one-line reviewed edit here).
///
/// <para>The hashed text is the OPERATOR'S GOAL and nothing else. Every lane-local decoration is deliberately
/// EXCLUDED — the planner's acceptance-criteria fold, its operator-feedback fold and its flat-plan constraint;
/// the supervisor projection's prepended session grounding — because a decorated string differs between the two
/// lanes, and between a first plan and a re-plan of the SAME task, and each difference re-rolls the arm with ~50%
/// probability. <c>LessonArmAgreementTests</c> drives both lanes' real composers and fails if either lane starts
/// feeding a decorated string again.</para>
/// </summary>
public static class LessonArms
{
    public const string Injected = "injected";
    public const string Withheld = "withheld";
    /// <summary>No current lesson existed to inject — outside the experiment entirely (never counted as a control).</summary>
    public const string None = "none";

    /// <summary>The lesson window BOTH lanes read, so the two treatments carry the same slice of the ledger.</summary>
    public const int TopK = 5;

    /// <summary>The arm for a lane whose current-lesson window holds <paramref name="currentLessonCount"/> entries: an EMPTY window is <see cref="None"/> (outside the experiment — never a control), anything else the deterministic assignment.</summary>
    public static string For(Guid teamId, string taskGoal, int currentLessonCount) => currentLessonCount == 0 ? None : Assign(teamId, taskGoal);

    /// <summary><paramref name="taskGoal"/> is the operator's UNDECORATED goal (see the class remarks) — trimmed, so trailing authoring whitespace is not a different experiment unit.</summary>
    public static string Assign(Guid teamId, string taskGoal)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(teamId + "\n" + taskGoal.Trim()));
        return (hash[0] & 1) == 0 ? Injected : Withheld;
    }

    /// <summary>One lesson as the prompt line, rendered ONCE for both lanes (each prefixes its own bullet) — so the two treatments cannot drift into differently-worded evidence.</summary>
    public static string Line(Lesson lesson) => $"[{lesson.FailureClass}] {lesson.WhatFailed} → {lesson.HowToApply}";
}
