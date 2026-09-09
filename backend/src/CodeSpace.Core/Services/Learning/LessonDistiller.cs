using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
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

    private const string SystemPrompt = "You are the post-mortem analyst for an autonomous coding platform. You are shown yesterday's failed or parked workflow runs (their error text, supervisor decision tape, and observed runtime capabilities) and the team's CURRENT lessons. Distill durable, actionable lessons a PLANNER can apply before the next run. Consolidate: update or invalidate an existing lesson rather than adding a near-duplicate. Cite only the run ids you were shown. Lessons must be concrete (name commands, files, repos as they appear), never generic advice. Applicability arrays are optional restrictions: use only values shown on EVERY cited run; use [] when the lesson is independent of that runtime dimension.";

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

        // Internal qualification/benchmark runs are measurement, never training input. Keeping them out of both
        // discovery and the team-local candidate query prevents a hidden oracle or evaluator failure from becoming
        // a prompt lesson in a later round.
        var teamIds = await _db.WorkflowRun.AsNoTracking()
            .Where(run => run.Purpose == null)
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

        var options = new InProcessStructuredModelOptions(teamId) { TierCeiling = InProcessStructuredModel.CheapBrainCeiling, Logger = _logger };
        if (await InProcessStructuredModel.ResolveAsync(_clients, _models, options, cancellationToken).ConfigureAwait(false) is not { } resolved)
        {
            _logger.LogWarning("Lesson distillation for team {TeamId} skipped — no structured-capable pool model (fail-closed)", teamId);
            return;
        }

        var (structured, pick) = resolved;
        var currentAt = DateTimeOffset.UtcNow;
        var touched = new List<Lesson>();
        var added = 0;
        var updated = 0;
        var invalidated = 0;
        var refused = 0;
        var scopes = 0;

        try
        {
            foreach (var group in ScopeGroups(candidates.Values))
            {
                var scope = group.Key;
                var scopedCandidates = group.ToDictionary(candidate => candidate.RunId);
                var current = await _db.Lesson
                    .Where(lesson => lesson.TeamId == teamId && lesson.Mode == scope.Mode && lesson.RepositoryId == scope.RepositoryId)
                    .Where(lesson => lesson.InvalidatedAt == null && lesson.ValidFrom <= currentAt && lesson.ExpiresAt > currentAt)
                    .OrderByDescending(lesson => lesson.ValidFrom).ThenBy(lesson => lesson.Id)
                    .Take(LessonReader.MaxTake)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                touched.AddRange(current);

                var completion = await structured.CompleteStructuredAsync(BuildRequest(pick, current, scopedCandidates.Values), cancellationToken).ConfigureAwait(false);
                var proposals = completion.Json.Deserialize<LessonProposals>(LessonDistillationSchema.Options) ?? new LessonProposals();
                var fold = LessonConsolidation.Apply(new LessonConsolidationRequest(current, proposals, scopedCandidates, teamId, completion.Model, DateTimeOffset.UtcNow));

                foreach (var rejection in fold.Rejections)
                    _logger.LogWarning("Lesson proposal refused for team {TeamId}, mode {Mode}, repository {RepositoryId}: {Reason}", teamId, scope.Mode, scope.RepositoryId, rejection);

                touched.AddRange(fold.Inserts);
                _db.Lesson.AddRange(fold.Inserts);
                added += fold.Inserts.Count;
                updated += fold.Updates;
                invalidated += fold.Invalidations;
                refused += fold.Rejections.Count;
                scopes++;
            }

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            foreach (var lesson in touched)
                if (_db.Entry(lesson).State != EntityState.Detached) _db.Entry(lesson).State = EntityState.Detached;
            throw;
        }

        _logger.LogInformation("Lesson distillation for team {TeamId}: {Added} added, {Updated} updated, {Invalidated} invalidated, {Refused} refused over {Runs} run(s) in {Scopes} scope(s)",
            teamId, added, updated, invalidated, refused, candidates.Count, scopes);
    }

    private static IEnumerable<IGrouping<LessonScope, CandidateRun>> ScopeGroups(IEnumerable<CandidateRun> candidates) =>
        candidates.GroupBy(candidate => new LessonScope(candidate.Mode, candidate.RepositoryId))
            .OrderBy(group => group.Key.Mode, StringComparer.Ordinal)
            .ThenBy(group => group.Key.RepositoryId);

    private async Task<IReadOnlyDictionary<Guid, CandidateRun>> LoadCandidatesAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var since = DateTimeOffset.UtcNow - Window;

        // Repeat the purpose guard here because callers can deliberately distill one team without using the sweep.
        var rows = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.TeamId == teamId && r.Purpose == null)
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

        var selected = fresh.Take(MaxRunsPerRound).ToList();
        var selectedIds = selected.Select(row => row.Id).ToList();
        var runtimeRows = await _db.AgentRun.AsNoTracking().Where(run => run.WorkflowRunId != null && selectedIds.Contains(run.WorkflowRunId.Value))
            .Select(run => new { RunId = run.WorkflowRunId!.Value, run.Harness, run.TaskJson, run.ResultJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var runtimes = runtimeRows.GroupBy(row => row.RunId).ToDictionary(group => group.Key, group => RuntimeFacts(group.Select(row => (row.Harness, row.TaskJson, row.ResultJson))));
        var candidates = new Dictionary<Guid, CandidateRun>();

        foreach (var row in selected)
        {
            var mode = await RunModeReader.DeriveAsync(_db, row.Id, teamId, cancellationToken).ConfigureAwait(false);
            var decisions = await _decisions.GetTerminalDecisionsAsync(row.Id, teamId, cancellationToken).ConfigureAwait(false);

            var lines = decisions.Take(12)
                .Select(d => $"{d.Sequence}. {d.DecisionKind} [{d.Status}]{(string.IsNullOrWhiteSpace(d.Error) ? "" : $" error: {Trim(d.Error, 200)}")}")
                .ToList();

            var status = row.CompletionParkedAt is not null ? "Parked" : row.Status.ToString();
            var runtime = runtimes.GetValueOrDefault(row.Id) ?? new CandidateRuntimeFacts([], [], []);
            candidates[row.Id] = new CandidateRun(row.Id, mode, row.ScopeRepositoryIds is [var sole] ? sole : null, status, Trim(row.Error, 500), lines)
            {
                Models = runtime.Models, Harnesses = runtime.Harnesses, Tools = runtime.Tools,
            };
        }

        return candidates;
    }

    private static CandidateRuntimeFacts RuntimeFacts(IEnumerable<(string Harness, string TaskJson, string? ResultJson)> rows)
    {
        var models = new List<string>();
        var harnesses = new List<string>();
        var tools = new List<string>();

        foreach (var row in rows)
        {
            harnesses.Add(row.Harness);
            var task = Deserialize<AgentTask>(row.TaskJson);
            var result = Deserialize<AgentRunResult>(row.ResultJson);
            if ((LessonApplicability.Normalize(result?.Model) ?? LessonApplicability.Normalize(task?.Model)) is { } effectiveModel) models.Add(effectiveModel);
            if (task?.Tools is { } taskTools) tools.AddRange(taskTools);
        }

        return new(LessonApplicability.Normalize(models), LessonApplicability.Normalize(harnesses), LessonApplicability.Normalize(tools));
    }

    private static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json, AgentJson.Options); }
        catch (JsonException) { return default; }
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
            sb.AppendLine($"{i + 1}. id={current[i].Id} [{current[i].FailureClass}] {current[i].WhatFailed} → {current[i].HowToApply}; applicability models=[{string.Join(", ", current[i].ApplicableModels)}], harnesses=[{string.Join(", ", current[i].ApplicableHarnesses)}], requiredTools=[{string.Join(", ", current[i].RequiredTools)}]");

        sb.AppendLine();
        sb.AppendLine("## Runs to learn from (cite ONLY these ids)");

        foreach (var run in candidates)
        {
            sb.AppendLine($"### run {run.RunId} — mode {run.Mode}, outcome {run.Status}");
            sb.AppendLine($"runtime models=[{string.Join(", ", run.Models)}]; harnesses=[{string.Join(", ", run.Harnesses)}]; tools=[{string.Join(", ", run.Tools)}]");
            if (!string.IsNullOrWhiteSpace(run.Error)) sb.AppendLine($"error: {run.Error}");
            foreach (var line in run.DecisionLines) sb.AppendLine(line);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string? Trim(string? text, int max) => text is null ? null : text.Length <= max ? text : text[..max] + "…";

    private sealed record LessonScope(string Mode, Guid? RepositoryId);
    private sealed record CandidateRuntimeFacts(IReadOnlyList<string> Models, IReadOnlyList<string> Harnesses, IReadOnlyList<string> Tools);
}
