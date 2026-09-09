using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Learning;

/// <summary>One candidate run as shown to the brain — the closed set a proposal may cite from.</summary>
public sealed record CandidateRun(Guid RunId, string Mode, Guid? RepositoryId, string Status, string? Error, IReadOnlyList<string> DecisionLines)
{
    public IReadOnlyList<string> Models { get; init; } = [];
    public IReadOnlyList<string> Harnesses { get; init; } = [];
    public IReadOnlyList<string> Tools { get; init; } = [];
}

internal sealed record LessonSelectors(IReadOnlyList<string> Models, IReadOnlyList<string> Harnesses, IReadOnlyList<string> Tools);

public sealed record LessonConsolidationRequest(IReadOnlyList<Lesson> Current, LessonProposals Proposals, IReadOnlyDictionary<Guid, CandidateRun> Candidates, Guid TeamId, string DistilledByModel, DateTimeOffset Now);

internal sealed record LessonMintContext(IReadOnlyDictionary<Guid, CandidateRun> Candidates, Guid TeamId, string DistilledByModel, DateTimeOffset Now);

/// <summary>The fold's effects: rows to insert, version updates, invalidations, and proposals refused with reasons (logged loudly — a rejection is a signal, never silence).</summary>
public sealed record LessonFold(IReadOnlyList<Lesson> Inserts, int Updates, int Invalidations, IReadOnlyList<string> Rejections);

/// <summary>
/// D1's consolidation fold — pure, pinned by unit test. Applies the brain's proposals against the team's CURRENT
/// lessons under the anti-confabulation rules: an add/update must cite only runs the prompt actually showed and
/// must cite at least one; an update/invalidate must name a CURRENT lesson id verbatim. Anything else is REJECTED
/// with a named reason — a hallucinated citation or id must never mint authority. Invalidation is one-way.
/// </summary>
public static class LessonConsolidation
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    public static LessonFold Apply(LessonConsolidationRequest request)
    {
        var (current, proposals, candidates, teamId, distilledByModel, now) = request;
        var mintContext = new LessonMintContext(candidates, teamId, distilledByModel, now);
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

                case "add" when TryResolveCitations(proposal, candidates, rejections) is { } cited && TryResolveSelectors(proposal, cited, candidates, rejections) is { } selectors:
                    inserts.Add(Mint(proposal, cited, selectors, mintContext));
                    break;

                case "update" when TryResolveCurrent(proposal, current, rejections) is { } target && TryResolveCitations(proposal, candidates, rejections) is { } freshCitations && TryResolveSelectors(proposal, freshCitations, candidates, rejections) is { } replacementSelectors && PreservesHistoricalApplicability(target, replacementSelectors, rejections):
                    target.InvalidatedAt = now;
                    inserts.Add(Replace(target, proposal, freshCitations, replacementSelectors, mintContext));
                    updates++;
                    break;

                case "invalidate" when TryResolveCurrent(proposal, current, rejections) is { } retired:
                    retired.InvalidatedAt = now;
                    retired.QualifiedAt = null;
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
            if (!Guid.TryParse(raw, out var id) || !candidates.ContainsKey(id))
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

    private static LessonSelectors? TryResolveSelectors(LessonProposal proposal, IReadOnlyList<Guid> cited, IReadOnlyDictionary<Guid, CandidateRun> candidates, List<string> rejections)
    {
        var selectors = new LessonSelectors(LessonApplicability.Normalize(proposal.ApplicableModels), LessonApplicability.Normalize(proposal.ApplicableHarnesses), LessonApplicability.Normalize(proposal.RequiredTools));
        var citedRuns = cited.Select(id => candidates[id]).ToList();
        var errors = new[]
        {
            LessonApplicability.Validate(selectors.Models, citedRuns.Select(run => run.Models), "applicableModels"),
            LessonApplicability.Validate(selectors.Harnesses, citedRuns.Select(run => run.Harnesses), "applicableHarnesses"),
            LessonApplicability.Validate(selectors.Tools, citedRuns.Select(run => run.Tools), "requiredTools"),
        }.Where(error => error is not null).Select(error => error!).ToList();

        if (errors.Count == 0) return selectors;
        rejections.AddRange(errors);
        return null;
    }

    private static bool PreservesHistoricalApplicability(Lesson target, LessonSelectors selectors, List<string> rejections)
    {
        var historical = new LessonSelectors(LessonApplicability.Normalize(target.ApplicableModels), LessonApplicability.Normalize(target.ApplicableHarnesses), LessonApplicability.Normalize(target.RequiredTools));
        var changed = new[]
        {
            (Name: "applicableModels", Before: historical.Models, After: selectors.Models),
            (Name: "applicableHarnesses", Before: historical.Harnesses, After: selectors.Harnesses),
            (Name: "requiredTools", Before: historical.Tools, After: selectors.Tools),
        }.FirstOrDefault(dimension => dimension.After.Count > 0 && !dimension.Before.SequenceEqual(dimension.After, StringComparer.Ordinal));

        if (changed.After is null) return true;
        rejections.Add($"{changed.Name} cannot specialize or change an existing lesson because its historical citations do not carry the new selector evidence");
        return false;
    }

    private static Lesson Mint(LessonProposal proposal, IReadOnlyList<Guid> cited, LessonSelectors selectors, LessonMintContext context)
    {
        var citedRuns = cited.Select(id => context.Candidates[id]).ToList();
        var repositories = citedRuns.Select(r => r.RepositoryId).Distinct().ToList();

        return new Lesson
        {
            Id = Guid.NewGuid(),
            TeamId = context.TeamId,
            Mode = citedRuns[0].Mode,
            RepositoryId = repositories is [{ } sole] ? sole : null,
            FailureClass = proposal.FailureClass ?? "",
            WhatFailed = proposal.WhatFailed ?? "",
            Why = proposal.Why ?? "",
            HowToApply = proposal.HowToApply ?? "",
            ApplicableModels = selectors.Models.ToList(),
            ApplicableHarnesses = selectors.Harnesses.ToList(),
            RequiredTools = selectors.Tools.ToList(),
            SourceRunIds = cited.ToList(),
            DistilledByModel = context.DistilledByModel,
            ValidFrom = context.Now,
            ExpiresAt = context.Now + Lifetime,
        };
    }

    private static Lesson Replace(Lesson target, LessonProposal proposal, IReadOnlyList<Guid> freshCitations, LessonSelectors selectors, LessonMintContext context)
    {
        return new Lesson
        {
            Id = Guid.NewGuid(),
            TeamId = target.TeamId,
            Mode = target.Mode,
            RepositoryId = target.RepositoryId,
            FailureClass = proposal.FailureClass ?? target.FailureClass,
            WhatFailed = proposal.WhatFailed ?? target.WhatFailed,
            Why = proposal.Why ?? target.Why,
            HowToApply = proposal.HowToApply ?? target.HowToApply,
            ApplicableModels = selectors.Models.ToList(),
            ApplicableHarnesses = selectors.Harnesses.ToList(),
            RequiredTools = selectors.Tools.ToList(),
            SourceRunIds = target.SourceRunIds.Union(freshCitations).ToList(),
            DistilledByModel = context.DistilledByModel,
            ValidFrom = context.Now,
            ExpiresAt = context.Now + Lifetime,
        };
    }
}
