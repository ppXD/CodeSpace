using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Learning;

/// <summary>One candidate run as shown to the brain — the closed set a proposal may cite from.</summary>
public sealed record CandidateRun(Guid RunId, string Mode, Guid? RepositoryId, string Status, string? Error, IReadOnlyList<string> DecisionLines);

/// <summary>The fold's effects: rows to insert, tracked rows mutated in place, and the proposals refused with reasons (logged loudly — a rejection is a signal, never silence).</summary>
public sealed record LessonFold(IReadOnlyList<Lesson> Inserts, int Updates, int Invalidations, IReadOnlyList<string> Rejections);

/// <summary>
/// D1's consolidation fold — pure, pinned by unit test. Applies the brain's proposals against the team's CURRENT
/// lessons under the anti-confabulation rules: an add/update must cite only runs the prompt actually showed and
/// must cite at least one; an update/invalidate must name a CURRENT lesson id verbatim. Anything else is REJECTED
/// with a named reason — a hallucinated citation or id must never mint authority. Invalidation is one-way.
/// </summary>
public static class LessonConsolidation
{
    public static LessonFold Apply(IReadOnlyList<Lesson> current, LessonProposals proposals, IReadOnlyDictionary<Guid, CandidateRun> candidates, Guid teamId, string distilledByModel, DateTimeOffset now)
    {
        var inserts = new List<Lesson>();
        var rejections = new List<string>();
        var updates = 0;
        var invalidations = 0;

        foreach (var proposal in proposals.Lessons)
        {
            switch (proposal.Action?.Trim().ToLowerInvariant())
            {
                case "noop":
                    break;

                case "add" when TryResolveCitations(proposal, candidates, rejections) is { } cited:
                    inserts.Add(Mint(proposal, cited, candidates, teamId, distilledByModel, now));
                    break;

                case "update" when TryResolveCurrent(proposal, current, rejections) is { } target && TryResolveCitations(proposal, candidates, rejections) is { } freshCitations:
                    Merge(target, proposal, freshCitations, now);
                    updates++;
                    break;

                case "invalidate" when TryResolveCurrent(proposal, current, rejections) is { } retired:
                    retired.InvalidatedAt = now;
                    invalidations++;
                    break;

                case "add" or "update" or "invalidate":
                    break;   // its Try* already recorded the named rejection

                default:
                    rejections.Add($"unknown action '{proposal.Action}' — the op vocabulary is closed");
                    break;
            }
        }

        return new LessonFold(inserts, updates, invalidations, rejections);
    }

    private static IReadOnlyList<Guid>? TryResolveCitations(LessonProposal proposal, IReadOnlyDictionary<Guid, CandidateRun> candidates, List<string> rejections)
    {
        var cited = new List<Guid>();

        foreach (var raw in proposal.SourceRunIds)
        {
            if (!Guid.TryParse(raw, out var id))
            {
                rejections.Add($"citation '{raw}' is not a run this round showed the brain — refused (a lesson may only cite what it was taught from)");
                return null;
            }

            cited.Add(id);
        }

        if (cited.Count > 0) return cited;

        rejections.Add("a lesson with no citations is no lesson — refused");
        return null;
    }

    private static Lesson? TryResolveCurrent(LessonProposal proposal, IReadOnlyList<Lesson> current, List<string> rejections)
    {
        if (Guid.TryParse(proposal.ExistingLessonId, out var id) && current.FirstOrDefault(l => l.Id == id) is { } lesson) return lesson;

        rejections.Add($"existingLessonId '{proposal.ExistingLessonId}' names no CURRENT lesson — refused (a hallucinated id must never mint authority)");
        return null;
    }

    private static Lesson Mint(LessonProposal proposal, IReadOnlyList<Guid> cited, IReadOnlyDictionary<Guid, CandidateRun> candidates, Guid teamId, string distilledByModel, DateTimeOffset now)
    {
        var citedRuns = cited.Select(id => candidates[id]).ToList();
        var repositories = citedRuns.Select(r => r.RepositoryId).Distinct().ToList();

        return new Lesson
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Mode = citedRuns[0].Mode,
            RepositoryId = repositories is [{ } sole] ? sole : null,
            FailureClass = proposal.FailureClass ?? "",
            WhatFailed = proposal.WhatFailed ?? "",
            Why = proposal.Why ?? "",
            HowToApply = proposal.HowToApply ?? "",
            SourceRunIds = cited.ToList(),
            DistilledByModel = distilledByModel,
            ValidFrom = now,
        };
    }

    private static void Merge(Lesson target, LessonProposal proposal, IReadOnlyList<Guid> freshCitations, DateTimeOffset now)
    {
        target.FailureClass = proposal.FailureClass ?? target.FailureClass;
        target.WhatFailed = proposal.WhatFailed ?? target.WhatFailed;
        target.Why = proposal.Why ?? target.Why;
        target.HowToApply = proposal.HowToApply ?? target.HowToApply;
        target.SourceRunIds = target.SourceRunIds.Union(freshCitations).ToList();
        target.LastModifiedDate = now;
    }
}
