using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace CodeSpace.Core.Services.Learning;

public interface ILessonReader
{
    /// <summary>Applicable, current and provenance-backed lessons for one prompt boundary.</summary>
    Task<IReadOnlyList<Lesson>> ListCurrentAsync(LessonReadRequest request, CancellationToken cancellationToken);
}

/// <summary>The runtime facts known at one prompt boundary. Null means unknown, which can admit only lessons that do not constrain that dimension.</summary>
public sealed record LessonRuntimeContext(Guid? RepositoryId, string? Model, string? Harness, IReadOnlyList<string>? Tools)
{
    public static LessonRuntimeContext General(Guid? repositoryId = null) => new(repositoryId, null, null, null);
}

/// <summary>The complete server-owned scope for a prompt-facing lesson lookup.</summary>
public sealed record LessonReadRequest(Guid TeamId, string Mode, LessonRuntimeContext Runtime, DateTimeOffset AsOf, int Take);

public sealed record LessonAvailabilityRequest(Guid TeamId, string Mode, Guid? RepositoryId, DateTimeOffset AsOf);

/// <summary>Arc D / D2 — the learning loop's read side: the injection reader over the lesson ledger.</summary>
public sealed class LessonReader : ILessonReader, IScopedDependency
{
    public const int MaxTake = 20;
    public const int MaxCandidateTake = 1;

    private readonly CodeSpaceDbContext _db;

    public LessonReader(CodeSpaceDbContext db) => _db = db;

    public async Task<IReadOnlyList<Lesson>> ListCurrentAsync(LessonReadRequest request, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(request.Take, 0, MaxTake);
        if (take == 0 || string.IsNullOrWhiteSpace(request.Mode)) return [];

        var model = LessonApplicability.Normalize(request.Runtime.Model);
        var harness = LessonApplicability.Normalize(request.Runtime.Harness);
        var tools = LessonApplicability.Normalize(request.Runtime.Tools);
        var scoped = Current(_db, new LessonAvailabilityRequest(request.TeamId, request.Mode, request.Runtime.RepositoryId, request.AsOf))
            .Where(lesson => lesson.ApplicableModels.Count == 0 || (model != null && lesson.ApplicableModels.Contains(model)))
            .Where(lesson => lesson.ApplicableHarnesses.Count == 0 || (harness != null && lesson.ApplicableHarnesses.Contains(harness)))
            .Where(lesson => lesson.RequiredTools.Count == 0 || (tools != null && lesson.RequiredTools.All(tool => tools.Contains(tool))));

        var qualified = await scoped.Where(lesson => lesson.QualifiedAt != null)
            .OrderByDescending(lesson => request.Runtime.RepositoryId != null && lesson.RepositoryId == request.Runtime.RepositoryId)
            .ThenByDescending(lesson => lesson.ValidFrom)
            .ThenBy(lesson => lesson.Id)
            .Take(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var candidateTake = Math.Min(MaxCandidateTake, take - qualified.Count);
        if (candidateTake == 0) return qualified;

        var candidates = await scoped.Where(lesson => lesson.QualifiedAt == null)
            .OrderByDescending(lesson => request.Runtime.RepositoryId != null && lesson.RepositoryId == request.Runtime.RepositoryId)
            .ThenByDescending(lesson => lesson.ValidFrom)
            .ThenBy(lesson => lesson.Id)
            .Take(candidateTake)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return qualified.Concat(candidates).ToList();
    }

    internal static Task<bool> HasCurrentAsync(CodeSpaceDbContext db, LessonAvailabilityRequest request, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(request.Mode) ? Task.FromResult(false) : Current(db, request).AnyAsync(cancellationToken);

    private static IQueryable<Lesson> Current(CodeSpaceDbContext db, LessonAvailabilityRequest request) => db.Lesson.AsNoTracking()
        .Where(lesson => lesson.TeamId == request.TeamId && lesson.Mode == request.Mode)
        .Where(lesson => lesson.ValidFrom <= request.AsOf && lesson.ExpiresAt > request.AsOf && (lesson.InvalidatedAt == null || lesson.InvalidatedAt > request.AsOf))
        .Where(lesson => lesson.QualificationSuppressedAt == null)
        .Where(lesson => lesson.SourceRunIds.Count > 0 && lesson.DistilledByModel != "")
        .Where(lesson => request.RepositoryId == null ? lesson.RepositoryId == null : lesson.RepositoryId == null || lesson.RepositoryId == request.RepositoryId);
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

    /// <summary>True only for a server-issued experiment value. Persisted or caller-provided unknown text is never trusted as prompt policy.</summary>
    public static bool IsKnown(string? arm) => arm is Injected or Withheld or None;

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
