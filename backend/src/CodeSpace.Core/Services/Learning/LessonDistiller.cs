using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace CodeSpace.Core.Services.Learning;

public interface ILessonDistiller
{
    /// <summary>One nightly round over every team with fresh failures — per-team faults are logged and never abort the sweep. Returns the number of teams distilled.</summary>
    Task<int> DistillAsync(CancellationToken cancellationToken);

    /// <summary>One team's round: window → not-yet-cited candidates → strong-model post-mortem → consolidation fold → ledger.</summary>
    Task DistillTeamAsync(Guid teamId, CancellationToken cancellationToken);
}

/// <summary>
/// Arc D / D1 — the nightly post-mortem distiller: reads the last day's FAILED and PARKED runs (error + decision
/// tape), asks the team's strongest structured model for consolidated lessons (the Mem0 op set over the team's
/// CURRENT lessons), and appends them to the lesson ledger with citations. Idempotent by provenance: a run any
/// lesson already cites is never re-distilled, so a re-run of the same window makes NO second model call.
/// Advisory like capability tiering — a faulty round leaves the ledger unchanged and never crashes the job.
/// </summary>
public sealed class LessonDistiller : ILessonDistiller, IScopedDependency
{
    /// <summary>Runs shown to the brain per round — the freshest first; anything past the cap is LOGGED as deferred, never silently dropped.</summary>
    public const int MaxRunsPerRound = 12;

    /// <summary>How far back a round looks — parked runs gate on their park stamp, failed runs on their terminal stamp.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private const string SystemPrompt = "You are the post-mortem analyst for an autonomous coding platform. You are shown yesterday's failed or parked workflow runs (their error text and supervisor decision tape) and the team's CURRENT lessons. Distill durable, actionable lessons a PLANNER can apply before the next run. Consolidate: update or invalidate an existing lesson rather than adding a near-duplicate. Cite only the run ids you were shown. Lessons must be concrete (name commands, files, repos as they appear), never generic advice.";

    private readonly ILLMClientRegistry _clients;
    private readonly IModelPoolSelector _models;
    private readonly ISupervisorDecisionLog _decisions;
    private readonly CodeSpaceDbContext _db;
    private readonly ILogger<LessonDistiller> _logger;

    public LessonDistiller(ILLMClientRegistry clients, IModelPoolSelector models, ISupervisorDecisionLog decisions, CodeSpaceDbContext db, ILogger<LessonDistiller> logger)
    {
        _clients = clients;
        _models = models;
        _decisions = decisions;
        _db = db;
        _logger = logger;
    }

    public async Task<int> DistillAsync(CancellationToken cancellationToken)
    {
        var since = DateTimeOffset.UtcNow - Window;

        var teamIds = await _db.WorkflowRun.AsNoTracking()
            .Where(r => (r.Status == WorkflowRunStatus.Failure && r.CompletedAt >= since) || (r.CompletionParkedAt != null && r.CompletionParkedAt >= since))
            .Select(r => r.TeamId).Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var distilled = 0;

        foreach (var teamId in teamIds)
        {
            try
            {
                await DistillTeamAsync(teamId, cancellationToken).ConfigureAwait(false);
                distilled++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Lesson distillation for team {TeamId} failed; its ledger is unchanged this round (advisory)", teamId);
            }
        }

        return distilled;
    }

    public async Task DistillTeamAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var candidates = await LoadCandidatesAsync(teamId, cancellationToken).ConfigureAwait(false);

        if (candidates.Count == 0) return;

        if (await InProcessStructuredModel.ResolveAsync(_clients, _models, teamId, cancellationToken, InProcessStructuredModel.CheapBrainCeiling).ConfigureAwait(false) is not { } resolved)
        {
            _logger.LogWarning("Lesson distillation for team {TeamId} skipped — no structured-capable pool model (fail-closed)", teamId);
            return;
        }

        var (structured, pick) = resolved;
        var current = await _db.Lesson.Where(l => l.TeamId == teamId && l.InvalidatedAt == null).ToListAsync(cancellationToken).ConfigureAwait(false);

        var completion = await structured.CompleteStructuredAsync(BuildRequest(pick, current, candidates.Values), cancellationToken).ConfigureAwait(false);

        var proposals = completion.Json.Deserialize<LessonProposals>(LessonDistillationSchema.Options) ?? new LessonProposals();
        var fold = LessonConsolidation.Apply(current, proposals, candidates, teamId, completion.Model, DateTimeOffset.UtcNow);

        foreach (var rejection in fold.Rejections)
            _logger.LogWarning("Lesson proposal refused for team {TeamId}: {Reason}", teamId, rejection);

        _db.Lesson.AddRange(fold.Inserts);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Lesson distillation for team {TeamId}: {Added} added, {Updated} updated, {Invalidated} invalidated, {Refused} refused over {Runs} run(s) by {Model}",
            teamId, fold.Inserts.Count, fold.Updates, fold.Invalidations, fold.Rejections.Count, candidates.Count, completion.Model);
    }

    private async Task<IReadOnlyDictionary<Guid, CandidateRun>> LoadCandidatesAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var since = DateTimeOffset.UtcNow - Window;

        var rows = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.TeamId == teamId)
            .Where(r => (r.Status == WorkflowRunStatus.Failure && r.CompletedAt >= since) || (r.CompletionParkedAt != null && r.CompletionParkedAt >= since))
            .OrderByDescending(r => r.CompletionParkedAt ?? r.CompletedAt)
            .Select(r => new { r.Id, r.Status, r.Error, r.ScopeRepositoryIds, r.CompletionParkedAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Provenance idempotence: a run ANY lesson already cites (current or invalidated) is never re-distilled.
        var cited = (await _db.Lesson.AsNoTracking().Where(l => l.TeamId == teamId).Select(l => l.SourceRunIds).ToListAsync(cancellationToken).ConfigureAwait(false))
            .SelectMany(ids => ids).ToHashSet();

        var fresh = rows.Where(r => !cited.Contains(r.Id)).ToList();

        if (fresh.Count > MaxRunsPerRound)
            _logger.LogInformation("Lesson distillation for team {TeamId}: {Deferred} candidate run(s) beyond the {Cap}-run cap deferred to the next round", teamId, fresh.Count - MaxRunsPerRound, MaxRunsPerRound);

        var candidates = new Dictionary<Guid, CandidateRun>();

        foreach (var row in fresh.Take(MaxRunsPerRound))
        {
            var mode = await RunModeReader.DeriveAsync(_db, row.Id, teamId, cancellationToken).ConfigureAwait(false);
            var decisions = await _decisions.GetTerminalDecisionsAsync(row.Id, teamId, cancellationToken).ConfigureAwait(false);

            var lines = decisions.Take(12)
                .Select(d => $"{d.Sequence}. {d.DecisionKind} [{d.Status}]{(string.IsNullOrWhiteSpace(d.Error) ? "" : $" error: {Trim(d.Error, 200)}")}")
                .ToList();

            var status = row.CompletionParkedAt is not null ? "Parked" : row.Status.ToString();
            candidates[row.Id] = new CandidateRun(row.Id, mode, row.ScopeRepositoryIds is [var sole] ? sole : null, status, Trim(row.Error, 500), lines);
        }

        return candidates;
    }

    private static StructuredLLMCompletionRequest BuildRequest(ModelPoolPick pick, IReadOnlyList<Lesson> current, IEnumerable<CandidateRun> candidates) => new()
    {
        Model = pick.ModelId,
        SystemPrompt = SystemPrompt,
        UserPrompt = BuildUserPrompt(current, candidates),
        JsonSchema = LessonDistillationSchema.ResponseSchema,
        MaxOutputTokens = 8192,
        Temperature = 0.0,
        Credential = pick.Credential,
    };

    /// <summary>Pinned by unit test: current lessons appear numbered WITH their ids (update/invalidate must name them verbatim); every run block carries its id, status, error, and decision tape.</summary>
    internal static string BuildUserPrompt(IReadOnlyList<Lesson> current, IEnumerable<CandidateRun> candidates)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Current lessons (update/invalidate by id; do not re-add near-duplicates)");
        if (current.Count == 0) sb.AppendLine("(none yet)");
        for (var i = 0; i < current.Count; i++)
            sb.AppendLine($"{i + 1}. id={current[i].Id} [{current[i].FailureClass}] {current[i].WhatFailed} → {current[i].HowToApply}");

        sb.AppendLine();
        sb.AppendLine("## Runs to learn from (cite ONLY these ids)");

        foreach (var run in candidates)
        {
            sb.AppendLine($"### run {run.RunId} — mode {run.Mode}, outcome {run.Status}");
            if (!string.IsNullOrWhiteSpace(run.Error)) sb.AppendLine($"error: {run.Error}");
            foreach (var line in run.DecisionLines) sb.AppendLine(line);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string? Trim(string? text, int max) => text is null ? null : text.Length <= max ? text : text[..max] + "…";
}
