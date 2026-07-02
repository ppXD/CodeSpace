using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The canonical SHAPE of a supervisor decision's recorded outcome JSON (PR-E E3) and the per-turn-per-spawn
/// AgentRun wait key (must-fix #1). Co-located with the turn concern: the executor WRITES these outcomes, the
/// turn service READS the staged-agent count from a replayed spawn/retry outcome (to re-classify the suspend
/// path on a Duplicate replay), and the executor stamps the wait IterationKey. A pure helper — no DB, no state.
///
/// <para>The spawn/retry outcome records its staged <c>agentRunIds</c> so a replay re-derives the SAME
/// park-on-K-agents classification WITHOUT re-staging, and a later <c>merge</c> can read the prior Attempt's
/// agent results by id. The <c>agentCount</c> field is the count the node parks on.</para>
/// </summary>
public static class SupervisorOutcome
{
    /// <summary>The wait IterationKey for the k-th agent a spawn/retry staged at turn N: <c>&lt;nodeId&gt;#turn{N}#{k}</c> (must-fix #1's full form, mirroring flow.map's <c>&lt;mapId&gt;#&lt;i&gt;</c>). Distinct per (turn, spawn-index) so K waits never collide.</summary>
    public static string AgentWaitKey(string nodeId, int turnNumber, int spawnIndex) => $"{nodeId}#turn{turnNumber}#{spawnIndex}";

    /// <summary>
    /// The NUMERIC spawn index parsed back off an <see cref="AgentWaitKey"/> (the trailing <c>#{k}</c>) — the ordering
    /// key for crash-recovery re-park. The index is NOT zero-padded, so a LEXICOGRAPHIC sort on the raw key scrambles
    /// it for K≥11 (<c>#0,#1,#10,#11,…,#2</c>), which would reorder a re-derived <c>agentRunIds</c> out of the authored
    /// <c>subtaskIds[i]</c> order every spawn-fan-out + the per-unit acceptance join depend on. Re-derivation must order
    /// by THIS instead. A malformed tail sorts LAST (<see cref="int.MaxValue"/>) — fail-legible, never a throw.
    /// </summary>
    public static int SpawnIndexOf(string iterationKey)
    {
        var hash = iterationKey.LastIndexOf('#');

        return hash >= 0 && hash + 1 < iterationKey.Length && int.TryParse(iterationKey[(hash + 1)..], out var k) ? k : int.MaxValue;
    }

    /// <summary>The SupervisorDecision self-advance wait IterationKey a synchronous (plan/merge) turn parks on: <c>&lt;nodeId&gt;#turn{N}</c> (must-fix #1; the per-turn root the spawn key's <c>#{k}</c> + ask key's <c>#ask</c> hang off). Distinct per turn so each turn's self-advance row never collides.</summary>
    public static string SelfAdvanceWaitKey(string nodeId, int turnNumber) => $"{nodeId}#turn{turnNumber}";

    /// <summary>Read the count of agent runs a recorded spawn/retry outcome staged (0 when the outcome has no <c>agentCount</c> — a synchronous verb's outcome). Best-effort: a malformed / absent field reads 0.</summary>
    public static int ReadStagedAgentCount(string? outcomeJson)
    {
        if (string.IsNullOrWhiteSpace(outcomeJson)) return 0;

        try
        {
            var root = JsonDocument.Parse(outcomeJson).RootElement;

            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("agentCount", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32()
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    /// <summary>The Action wait IterationKey an ask_human turn parks on: <c>&lt;nodeId&gt;#turn{N}#ask</c> (mirroring the spawn key's <c>#turn{N}#{k}</c> shape). Distinct per turn so a later ask_human turn never collides with this one.</summary>
    public static string HumanWaitKey(string nodeId, int turnNumber) => $"{nodeId}#turn{turnNumber}#ask";

    /// <summary>Read the human-wait correlation token a recorded ask_human outcome posted its question card on (null when the outcome has none — a non-ask_human verb, or an ask_human that degraded to a no-surface stop). A replay re-derives the SAME park-on-human classification + token WITHOUT re-posting.</summary>
    public static string? ReadHumanWaitToken(string? outcomeJson)
    {
        if (string.IsNullOrWhiteSpace(outcomeJson)) return null;

        try
        {
            var root = JsonDocument.Parse(outcomeJson).RootElement;

            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("askHumanToken", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Read the question an ask_human outcome asked (null when absent/malformed) — folded with the recorded answer into the next turn's context so the decider sees "you asked X".</summary>
    public static string? ReadAskHumanQuestion(string? outcomeJson) => ReadStringField(outcomeJson, "question");

    /// <summary>
    /// Build an ask_human outcome JSON from its parts — the question, the question-card wait token, and the
    /// human's answer (null until answered). The single canonical shape the executor records on first execution
    /// (answer null) and the rehydrate FOLD re-stamps once the human's answer durably exists, so the decider
    /// reads "you asked X, the human answered Y" off the next turn's prior-decision outcome. Pure + deterministic.
    /// </summary>
    public static string FoldAnswer(string? question, string token, string? answer) =>
        JsonSerializer.Serialize(new { question, askHumanToken = token, answer }, AgentJson.Options);

    /// <summary>Read the human's recorded answer text from an ask_human outcome (null until the wait resolved + the answer was folded in). The decider sees "you asked X, the human answered Y" on the next turn.</summary>
    public static string? ReadAskHumanAnswer(string? outcomeJson) => ReadStringField(outcomeJson, "answer");

    /// <summary>Read the human's free-text answer (the <c>comment</c>) from a resolved Action wait's <c>{ action, by, comment }</c> payload. Empty string when absent/malformed (a click with no comment). The rehydrate fold AND the executor's resolved-wait recovery both read the answer through here.</summary>
    public static string ReadAnswerComment(string? payloadJson) => ReadStringField(payloadJson, "comment") ?? "";

    /// <summary>The model-authored closing line a <c>stop</c> outcome recorded — the <c>summary</c> field <c>ExecuteStop</c> wrote to <c>{ stopped, outcome, summary }</c>. The run's "here's what I did" narration; null when absent / malformed.</summary>
    public static string? ReadStopSummary(string? outcomeJson) => ReadStringField(outcomeJson, "summary");

    /// <summary>Best-effort read of a top-level string field from an outcome object (null when absent / malformed / not a string).</summary>
    private static string? ReadStringField(string? outcomeJson, string field)
    {
        if (string.IsNullOrWhiteSpace(outcomeJson)) return null;

        try
        {
            var root = JsonDocument.Parse(outcomeJson).RootElement;

            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Read the agent-run ids a recorded spawn/retry outcome staged, in spawn order. Empty when absent/malformed. Used by <c>merge</c> to read prior Attempt results by id.</summary>
    public static IReadOnlyList<Guid> ReadStagedAgentRunIds(string? outcomeJson)
    {
        if (string.IsNullOrWhiteSpace(outcomeJson)) return Array.Empty<Guid>();

        try
        {
            var root = JsonDocument.Parse(outcomeJson).RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("agentRunIds", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<Guid>();

            return arr.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String && Guid.TryParse(e.GetString(), out var id) ? id : (Guid?)null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<Guid>();
        }
    }

    /// <summary>
    /// Project ONE spawned agent's terminal facts into the compact, decider-visible <see cref="SupervisorAgentResult"/>
    /// (SOTA #2) — the SINGLE source of truth both the rehydrate fold (decider view) and the <c>merge</c> executor
    /// consume, so the two can never drift on which fields an agent exposes. <paramref name="statusName"/> is the
    /// authoritative AgentRun ROW status; <paramref name="rowError"/> is the ROW error (a cancelled/abandoned agent
    /// sets it with a NULL <paramref name="resultJson"/>), so the error surfaces even when the run wrote no result.
    /// Reads only bounded fields off the result — never the patch/transcript — so it needs no artifact-store fetch
    /// and stays a pure function of immutable post-terminal state (replay-deterministic).
    /// </summary>
    public static SupervisorAgentResult ProjectCompact(Guid agentRunId, string statusName, string? rowError, string? resultJson, string? model = null)
    {
        var result = string.IsNullOrWhiteSpace(resultJson) ? null : TryDeserializeResult(resultJson);

        return new SupervisorAgentResult
        {
            AgentRunId = agentRunId,
            Status = statusName,
            Summary = result?.Summary,
            Error = result?.Error ?? rowError,
            ChangedFiles = result?.ChangedFiles ?? Array.Empty<string>(),
            ProducedBranch = result?.ProducedBranch,
            // Resolver loop #379 S7-B/S7-C0: carry the agent's per-repo outcomes into the compact so the per-repo
            // resolution loop reads each repo's pushed branch straight off the ledger (replay-deterministic). The
            // unbounded per-repo DIFF is stripped (the merge reads it off the DB AgentRunResult, never this compact),
            // exactly as the compact omits the top-level Patch — so the ledger stays token-cheap. Empty for a
            // single-repo run → the top-level ProducedBranch/ChangedFiles remain the one outcome (behaviour-identical).
            RepositoryResults = StripPerRepoPatches(result?.RepositoryResults),
            // SOTA #4: the priced inputs ride inline so the cost bound sums realized spend straight off the durable
            // outcome (no new query, replay-deterministic). Tokens come from the result; the model is the agent's
            // task model (passed in by the rehydrate fold, which has TaskJson) — null when the caller has no model.
            InputTokens = result?.TokenUsage?.InputTokens ?? 0,
            OutputTokens = result?.TokenUsage?.OutputTokens ?? 0,
            Model = model,
        };
    }

    /// <summary>
    /// The summed USD spend of these compact results, priced via <see cref="AgentCostPricing"/> (SOTA #4) — the pure
    /// figure the supervisor's cost bound compares to <c>MaxCostUsd</c>. An unpriceable result (unknown/blank model,
    /// or a result folded before token fields existed → 0 tokens) contributes 0 (fail-open), so a usage-silent agent
    /// can never spuriously inflate the bill nor block the run; the agent-COUNT cap still bounds it.
    ///
    /// <para>NOTE — intentional asymmetry with the read plane (<c>TeamCostService</c>): a KNOWN-model agent that
    /// captured no usage prices here to a REAL $0 (tokens default to 0), whereas the bill treats it as unknown-cost.
    /// Both contribute 0, so the strict <c>&gt;</c> cap is unaffected either way; the divergence only matters if a
    /// future change surfaces unknown-cost FROM the fold — align <see cref="ProjectCompact"/> then, not now.</para>
    /// </summary>
    public static decimal SpendUsd(IReadOnlyList<SupervisorAgentResult> agentResults) =>
        agentResults.Sum(r => AgentCostPricing.CostUsd(r.Model, r.InputTokens, r.OutputTokens) ?? 0m);

    /// <summary>
    /// True when a staging decision's folded agent results carry REAL settled EVIDENCE of forward progress — at
    /// least one agent that SUCCEEDED, or that produced a concrete artifact (a non-empty git diff, a pushed branch,
    /// or a per-repo result). This is the deterministic signal the no-progress streak resets on: a wave of agents
    /// that ALL failed with no diff/branch produced nothing, so it must NOT count as progress — the prior staged-COUNT
    /// heuristic did, letting a loop that keeps spawning never-succeeding agents never trip the stall bound. Generic:
    /// reads ONLY the git-ground-truth fold (ChangedFiles/ProducedBranch) + the terminal status, no task knowledge.
    /// Empty/absent results (not-yet-folded / malformed) read as NO evidence — fail-toward-stall, the safe direction.
    /// </summary>
    public static bool HasSettledEvidence(IReadOnlyList<SupervisorAgentResult> agentResults) =>
        agentResults.Any(ResultHasEvidence);

    private static bool ResultHasEvidence(SupervisorAgentResult result) =>
        // Loopability slice 3: an OBJECTIVELY-REJECTED unit is NOT settled evidence — even if it pushed a branch or
        // self-reported Succeeded. Without this, a unit that pushes a branch but FAILS its per-unit acceptance resets
        // the no-progress streak (ProducedBranch counts below), so an acceptance-failing retry loop never trips the
        // stall bound (only the total-spawn cap), burning the budget on never-passing work. An ungraded unit
        // (AcceptancePassed null — no per-unit contract, the pre-slice case) is byte-identically unaffected.
        result.AcceptancePassed != false
        && (string.Equals(result.Status, nameof(AgentRunStatus.Succeeded), StringComparison.Ordinal)
            || result.ChangedFiles.Count > 0
            || !string.IsNullOrEmpty(result.ProducedBranch)
            || result.RepositoryResults.Any(repo => !string.IsNullOrEmpty(repo.ProducedBranch) || repo.ChangedFiles.Count > 0));

    /// <summary>
    /// Project the per-repo results into the COMPACT (decider-visible, durable-ledger) shape: the bounded per-repo
    /// facts (alias / repository id / produced branch / base / changed files) MINUS the unbounded per-repo diff
    /// (<c>Patch</c> + <c>PatchArtifactId</c> cleared) — the same reason the compact omits the top-level Patch. The
    /// merge's per-repo integrate reads the FULL diff off the DB <c>AgentRunResult</c>, never off this compact, so the
    /// ledger stays token-cheap. Empty (never null) for a single-repo run. Reuses the one <see cref="RepositoryRunResult"/>
    /// noun (no near-duplicate compact shape) — the projection just clears the heavy fields.
    /// </summary>
    private static IReadOnlyList<RepositoryRunResult> StripPerRepoPatches(IReadOnlyList<RepositoryRunResult>? repos) =>
        repos is null or { Count: 0 }
            ? Array.Empty<RepositoryRunResult>()
            : repos.Select(r => r.WithoutDiff()).ToList();

    /// <summary>Best-effort deserialize of a persisted <c>AgentRunResult</c> (null on malformed) — the compact projection tolerates a corrupt result the same way the merge path does.</summary>
    private static AgentRunResult? TryDeserializeResult(string resultJson)
    {
        try { return JsonSerializer.Deserialize<AgentRunResult>(resultJson, AgentJson.Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Fold the spawned agents' COMPACT results into a spawn/retry decision's recorded outcome (SOTA #2), ADDITIVELY:
    /// the existing <c>agentRunIds</c> + <c>agentCount</c> are read OFF THE INPUT and re-emitted byte-intact (so the
    /// E5 counters that read <c>agentCount</c> are unperturbed), with an <c>agentResults</c> array appended. Returns
    /// the input UNCHANGED when it staged no agents (a zero-agent spawn keeps its <c>note</c> field — re-emitting a
    /// fixed shape would drop it + trigger a spurious write). Deterministic + idempotent: same terminal agents →
    /// same bytes, so the rehydrate persist no-ops after the first post-barrier stamp.
    /// </summary>
    public static string FoldAgentResults(string? spawnOutcomeJson, IReadOnlyList<SupervisorAgentResult> agentResults)
    {
        var agentRunIds = ReadStagedAgentRunIds(spawnOutcomeJson);

        if (agentRunIds.Count == 0) return spawnOutcomeJson ?? "";

        var agentCount = ReadStagedAgentCount(spawnOutcomeJson);

        return JsonSerializer.Serialize(new { agentRunIds, agentCount, agentResults }, AgentJson.Options);
    }

    /// <summary>
    /// Fold an OBJECTIVE acceptance grade onto an already-agent-folded resolve outcome (resolver loop #379, L4 A3) — the
    /// server-run verdict that REPLACES the resolver's self-reported marker. Re-emits the existing
    /// <c>agentRunIds</c>/<c>agentCount</c>/<c>agentResults</c> BYTE-INTACT (read off the input, exactly as
    /// <see cref="FoldAgentResults"/>) and appends a single <c>acceptanceGrade</c> object, so the only byte change is the
    /// new key — the rehydrate fold runs this AT MOST ONCE (guarded by <see cref="ReadAcceptanceGradePassed"/> having no
    /// value yet), persists it, and every later replay reads the folded verdict off the durable tape (the grade I/O
    /// never re-runs). Pure.
    /// </summary>
    public static string FoldAcceptanceGrade(string? resolveOutcomeJson, bool passed, string detail)
    {
        var agentRunIds = ReadStagedAgentRunIds(resolveOutcomeJson);
        var agentCount = ReadStagedAgentCount(resolveOutcomeJson);
        var agentResults = ReadAgentResults(resolveOutcomeJson);

        return JsonSerializer.Serialize(new { agentRunIds, agentCount, agentResults, acceptanceGrade = new { passed, detail } }, AgentJson.Options);
    }

    /// <summary>
    /// Append an OBJECTIVE acceptance grade onto an ARBITRARY decision outcome, preserving every existing key verbatim
    /// (L4 P1 — the terminal-STOP analogue of <see cref="FoldAcceptanceGrade"/>). Unlike the resolve fold, which re-emits
    /// the fixed resolve shape, this is a GENERIC additive merge: it copies the outcome's own keys (a stop's
    /// <c>stopped</c>/<c>outcome</c>/<c>summary</c>) in order and adds a single <c>acceptanceGrade</c> object — so the
    /// stop's shape is never corrupted. Read back by the shape-agnostic <see cref="ReadAcceptanceGradePassed"/> (the same
    /// once-guard + verdict reader the resolve path uses). A null/blank/non-object input starts from an empty object.
    /// Pure + deterministic (keys preserved in source order, grade appended last).
    /// </summary>
    public static string AppendAcceptanceGrade(string? outcomeJson, bool passed, string detail)
    {
        var merged = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(outcomeJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(outcomeJson);

                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        merged[prop.Name] = prop.Value.Clone();   // Clone so the value outlives the JsonDocument's dispose
            }
            catch (JsonException) { merged.Clear(); }   // unparseable → start from an empty object (defensive; a stop outcome is always valid)
        }

        merged["acceptanceGrade"] = new { passed, detail };

        return JsonSerializer.Serialize(merged, AgentJson.Options);
    }

    /// <summary>
    /// Read the folded objective acceptance verdict off a resolve outcome (L4 A3): <c>true</c>/<c>false</c> = the
    /// server grade's pass/fail, <c>null</c> = NO grade was folded (no operator acceptance command configured), so the
    /// caller falls back to the self-reported marker — byte-identical to pre-A3. Best-effort + pure. Doubles as the
    /// fold's "already graded → don't re-grade" once-guard (a non-null value means the grade is durable on the tape).
    /// </summary>
    public static bool? ReadAcceptanceGradePassed(string? resolveOutcomeJson)
    {
        if (string.IsNullOrWhiteSpace(resolveOutcomeJson)) return null;

        try
        {
            var root = JsonDocument.Parse(resolveOutcomeJson).RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("acceptanceGrade", out var grade) || grade.ValueKind != JsonValueKind.Object)
                return null;

            return grade.TryGetProperty("passed", out var passed) && passed.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? passed.GetBoolean()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Read the on-disk INTEGRATION block a <c>merge</c> outcome records (SOTA #3 / resolver loop #379) into the
    /// compact, decider-visible <see cref="SupervisorIntegrationOutcome"/> — null when the outcome carries no
    /// <c>integration</c> object (the gate was off, or the verb wasn't a merge) or it's malformed. Aggregates the
    /// per-contribution <c>conflictedFiles</c> (deduped, order-preserved) + the preserved <c>fallbackBranch</c>es so
    /// the decider sees one legible "these files conflicted, these branches are preserved" signal — across the
    /// top-level <c>outcomes</c> (a single-repo block) OR every <c>repositories[].outcomes</c> (a multi-repo block,
    /// S7-C), so a multi-repo conflict is legible off the SAME reader. Best-effort + pure.
    /// </summary>
    public static SupervisorIntegrationOutcome? ReadIntegration(string? outcomeJson)
    {
        if (string.IsNullOrWhiteSpace(outcomeJson)) return null;

        try
        {
            var root = JsonDocument.Parse(outcomeJson).RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("integration", out var integration) || integration.ValueKind != JsonValueKind.Object)
                return null;

            if (!integration.TryGetProperty("status", out var statusEl) || statusEl.ValueKind != JsonValueKind.String)
                return null;

            var conflictedFiles = new List<string>();
            var preservedBranches = new List<string>();

            // Multi-repo (S7-C): collect across EVERY repo's outcomes; single-repo: the top-level outcomes array. A
            // multi-repo block has no single integratedBranch — the per-repo branches live in repositories[] (S7-D).
            if (integration.TryGetProperty("repositories", out var repositories) && repositories.ValueKind == JsonValueKind.Array)
            {
                foreach (var repo in repositories.EnumerateArray())
                    if (repo.ValueKind == JsonValueKind.Object && repo.TryGetProperty("outcomes", out var repoOutcomes))
                        CollectOutcomeDetail(repoOutcomes, conflictedFiles, preservedBranches);
            }
            else if (integration.TryGetProperty("outcomes", out var outcomes))
            {
                CollectOutcomeDetail(outcomes, conflictedFiles, preservedBranches);
            }

            return new SupervisorIntegrationOutcome
            {
                Status = statusEl.GetString()!,
                Reason = integration.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
                IntegratedBranch = integration.TryGetProperty("integratedBranch", out var ib) && ib.ValueKind == JsonValueKind.String ? ib.GetString() : null,
                ConflictedFiles = conflictedFiles,
                PreservedBranches = preservedBranches,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Accumulate one <c>outcomes</c> array's conflicted files + preserved fallback branches into the running aggregate (deduped, order-preserved). The single per-contribution reader both the single-repo top-level outcomes and each multi-repo repository's outcomes feed, so the two shapes can't drift.</summary>
    private static void CollectOutcomeDetail(JsonElement outcomes, List<string> conflictedFiles, List<string> preservedBranches)
    {
        if (outcomes.ValueKind != JsonValueKind.Array) return;

        foreach (var o in outcomes.EnumerateArray())
        {
            if (o.ValueKind != JsonValueKind.Object) continue;

            if (o.TryGetProperty("conflictedFiles", out var files) && files.ValueKind == JsonValueKind.Array)
                foreach (var f in files.EnumerateArray())
                    if (f.ValueKind == JsonValueKind.String && f.GetString() is { Length: > 0 } path && !conflictedFiles.Contains(path))
                        conflictedFiles.Add(path);

            if (o.TryGetProperty("fallbackBranch", out var fb) && fb.ValueKind == JsonValueKind.String && fb.GetString() is { Length: > 0 } branch && !preservedBranches.Contains(branch))
                preservedBranches.Add(branch);
        }
    }

    /// <summary>
    /// Read the build/test VERDICT off a <c>resolve</c> decision's folded outcome (resolver loop #379, S3): the
    /// resolver agent's compact result drives it — <see cref="SupervisorResolutionVerdict.Unknown"/> when no result is
    /// folded yet (still parked), <see cref="SupervisorResolutionVerdict.Verified"/> when the resolver SUCCEEDED AND
    /// its summary carries <see cref="SupervisorResolverRecipe.TestsPassedMarker"/> (the instruction-encoded green
    /// signal), else <see cref="SupervisorResolutionVerdict.Unverified"/>. Pure + deterministic; S4 gates acceptance
    /// on <c>Verified</c>. A multi-result outcome (shouldn't happen for resolve — it stages K=1) takes the first.
    /// </summary>
    public static SupervisorResolutionVerdict ReadResolutionVerdict(string? resolveOutcomeJson)
    {
        var results = ReadAgentResults(resolveOutcomeJson);

        if (results.Count == 0) return SupervisorResolutionVerdict.Unknown;

        var resolver = results[0];

        var markerVerified = string.Equals(resolver.Status, "Succeeded", StringComparison.OrdinalIgnoreCase)
            && resolver.Summary?.Contains(SupervisorResolverRecipe.TestsPassedMarker, StringComparison.Ordinal) == true;

        // A3: when a server grade was folded (an operator acceptance command ran objectively against the resolver's
        // branch), the verdict is OBJECTIVE — Verified iff the grade passed AND the self-report marker still holds (AND,
        // so the server check can only TIGHTEN, never manufacture, acceptance). Absent grade → the exact pre-A3
        // marker-only read (byte-identical, no idempotency-key drift). This is a PURE read off already-folded bytes —
        // the grade I/O ran once at the fold, never here at the accept boundary.
        var gradePassed = ReadAcceptanceGradePassed(resolveOutcomeJson);

        if (gradePassed.HasValue)
            return gradePassed.Value && markerVerified ? SupervisorResolutionVerdict.Verified : SupervisorResolutionVerdict.Unverified;

        return markerVerified ? SupervisorResolutionVerdict.Verified : SupervisorResolutionVerdict.Unverified;
    }

    /// <summary>
    /// Fold the run's FINAL reviewable integrated branch off the durable decision tape (resolver loop #379, S5) — the
    /// head a downstream <c>git.open_pr</c> / <c>git.open_change_set</c> node targets. A single reverse walk (latest
    /// decision wins) over two sources, uniformly: a <c>merge</c> that integrated CLEAN surfaces its
    /// <c>integration.integratedBranch</c>; a VERIFIED <c>resolve</c> surfaces the resolver's OWN pushed branch (the
    /// reconciled merge IS the resolver's branch — see <c>RealSupervisorActionExecutor.Integrate.cs</c>). Null when
    /// neither exists yet (an analysis-only run, or a conflict that was never resolved). Pure + replay-deterministic.
    /// </summary>
    public static string? ReadFinalIntegratedBranch(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        for (var i = priorDecisions.Count - 1; i >= 0; i--)
        {
            var decision = priorDecisions[i];

            if (decision.DecisionKind == SupervisorDecisionKinds.Merge && ReadIntegration(decision.OutcomeJson) is { IntegratedBranch: { Length: > 0 } cleanBranch })
                return cleanBranch;

            if (ResolvedBranch(decision) is { } resolvedBranch) return resolvedBranch;

            // A spawn / retry (or an unverified / branchless resolve) that NOTHING later merged or resolved means the
            // run's latest work is UN-combined — there is no clean reviewable head, and an earlier branch must not be
            // surfaced past fresh un-integrated work (a downstream open_pr would ship a PR missing it). This mirrors
            // Part B's disqualifier (AcceptedResolutionBranch): the most-recent agent-staging decision must itself be
            // the accepted resolution, else the integrator runs / there is nothing clean to surface.
            if (SupervisorDecisionKinds.StagesAgents(decision.DecisionKind)) return null;
        }

        return null;
    }

    /// <summary>
    /// Fold the run's FINAL per-repo reviewable integrated branches off the durable tape (resolver loop #379, S7-D1) —
    /// the MULTI-repo complement of <see cref="ReadFinalIntegratedBranch"/> (which is single-valued and so empty for a
    /// multi-repo run). A reverse walk (latest wins) returns the most recent <c>merge</c> whose per-repo
    /// <c>repositories[]</c> block carries any CLEAN repo (each with its own integrated branch); a fresh spawn / retry /
    /// resolve that nothing later merged is the same BARRIER as <see cref="ReadFinalIntegratedBranch"/> (don't surface a
    /// stale set past un-integrated work). EMPTY for a single-repo run (no <c>repositories[]</c>) — it surfaces the
    /// single <see cref="ReadFinalIntegratedBranch"/> instead. Pure + replay-deterministic.
    /// </summary>
    public static IReadOnlyList<SupervisorRepositoryBranch> ReadFinalRepositoryBranches(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        for (var i = priorDecisions.Count - 1; i >= 0; i--)
        {
            var decision = priorDecisions[i];

            if (decision.DecisionKind == SupervisorDecisionKinds.Merge)
            {
                var branches = ReadMergeRepositoryBranches(decision.OutcomeJson);
                if (branches.Count > 0) return branches;
            }

            // A VERIFIED MULTI-repo resolve surfaces the resolver's OWN per-repo reconciled branches (S7-D2) — checked
            // BEFORE the barrier (mirroring ReadFinalIntegratedBranch's ResolvedBranch-before-barrier), so a run that
            // STOPPED right after the resolution (no subsequent merge) still surfaces its per-repo heads.
            var resolved = ResolvedRepositoryBranches(decision);
            if (resolved.Count > 0) return resolved;

            // The same disqualifier as ReadFinalIntegratedBranch: fresh agent-staging work nothing later merged means
            // there is no clean per-repo set to surface (an earlier merge's branches would be stale past it).
            if (SupervisorDecisionKinds.StagesAgents(decision.DecisionKind)) return Array.Empty<SupervisorRepositoryBranch>();
        }

        return Array.Empty<SupervisorRepositoryBranch>();
    }

    /// <summary>Read the CLEAN per-repo integrated branches off ONE multi-repo merge's <c>integration.repositories[]</c> block — each repo whose status is <c>Clean</c> with a non-empty <c>integratedBranch</c>. Empty for a single-repo merge (no <c>repositories[]</c>) or a block with no clean repos. Best-effort + pure.</summary>
    private static IReadOnlyList<SupervisorRepositoryBranch> ReadMergeRepositoryBranches(string? outcomeJson)
    {
        if (string.IsNullOrWhiteSpace(outcomeJson)) return Array.Empty<SupervisorRepositoryBranch>();

        try
        {
            var root = JsonDocument.Parse(outcomeJson).RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("integration", out var integration) || integration.ValueKind != JsonValueKind.Object)
                return Array.Empty<SupervisorRepositoryBranch>();

            if (!integration.TryGetProperty("repositories", out var repositories) || repositories.ValueKind != JsonValueKind.Array)
                return Array.Empty<SupervisorRepositoryBranch>();

            var branches = new List<SupervisorRepositoryBranch>();

            foreach (var repo in repositories.EnumerateArray())
            {
                if (repo.ValueKind != JsonValueKind.Object) continue;

                if (!(repo.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() == "Clean")) continue;

                if (repo.TryGetProperty("integratedBranch", out var ib) && ib.ValueKind == JsonValueKind.String && ib.GetString() is { Length: > 0 } branch)
                    branches.Add(new SupervisorRepositoryBranch
                    {
                        RepositoryId = repo.TryGetProperty("repositoryId", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var g) ? g : null,
                        Alias = repo.TryGetProperty("alias", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "",
                        SourceBranch = branch,
                        TargetBranch = repo.TryGetProperty("baseBranch", out var bb) && bb.ValueKind == JsonValueKind.String ? bb.GetString() ?? "" : "",
                    });
            }

            return branches;
        }
        catch (JsonException)
        {
            return Array.Empty<SupervisorRepositoryBranch>();
        }
    }

    /// <summary>
    /// The SINGLE reviewable branch a VERIFIED SINGLE-repo <c>resolve</c> decision contributes — its first folded
    /// resolver agent's pushed branch — or null when the decision isn't a resolve, the resolution wasn't
    /// <see cref="SupervisorResolutionVerdict.Verified"/>, the resolver pushed nothing, OR the resolver was MULTI-repo
    /// (it has per-repo <c>RepositoryResults</c> → there is no single branch; use <see cref="ResolvedRepositoryBranches"/>).
    /// The SINGLE encoding of "an accepted single-repo resolution's tested branch" both the final-branch reader
    /// (<see cref="ReadFinalIntegratedBranch"/>) and the merge short-circuit (<c>RealSupervisorActionExecutor</c>) share,
    /// so the acceptance rule can never drift (Rule 7). Pure + replay-deterministic.
    /// </summary>
    public static string? ResolvedBranch(SupervisorPriorDecision decision)
    {
        if (decision.DecisionKind != SupervisorDecisionKinds.Resolve) return null;

        if (ReadResolutionVerdict(decision.OutcomeJson) != SupervisorResolutionVerdict.Verified) return null;

        var resolver = ReadAgentResults(decision.OutcomeJson).FirstOrDefault();

        // A MULTI-repo resolver has per-repo RepositoryResults — its top-level ProducedBranch mirrors only the PRIMARY
        // repo, so surfacing it as THE single integrated branch would drop every other repo. The per-repo branches go
        // through ResolvedRepositoryBranches instead; this single-branch encoding is single-repo only.
        if (resolver is null || resolver.RepositoryResults.Count > 0) return null;

        return resolver.ProducedBranch is { Length: > 0 } branch ? branch : null;
    }

    /// <summary>
    /// The PER-REPO reviewable branches a VERIFIED MULTI-repo <c>resolve</c> decision contributes (resolver loop #379,
    /// S7-D2) — the multi-repo resolver's own <c>RepositoryResults</c> (each repo's reconciled, pushed branch). Empty
    /// when the decision isn't a verified resolve OR the resolver was single-repo (use <see cref="ResolvedBranch"/>).
    /// The SINGLE encoding of "an accepted multi-repo resolution's per-repo branches" both the node-output reader
    /// (<see cref="ReadFinalRepositoryBranches"/>) and the merge short-circuit share (Rule 7). Pure + replay-deterministic.
    /// </summary>
    public static IReadOnlyList<SupervisorRepositoryBranch> ResolvedRepositoryBranches(SupervisorPriorDecision decision)
    {
        if (decision.DecisionKind != SupervisorDecisionKinds.Resolve) return Array.Empty<SupervisorRepositoryBranch>();

        if (ReadResolutionVerdict(decision.OutcomeJson) != SupervisorResolutionVerdict.Verified) return Array.Empty<SupervisorRepositoryBranch>();

        var resolver = ReadAgentResults(decision.OutcomeJson).FirstOrDefault();

        if (resolver is null) return Array.Empty<SupervisorRepositoryBranch>();

        return resolver.RepositoryResults
            .Where(r => !string.IsNullOrEmpty(r.ProducedBranch))
            .Select(r => new SupervisorRepositoryBranch { RepositoryId = r.RepositoryId, Alias = r.Alias, SourceBranch = r.ProducedBranch!, TargetBranch = r.BaseBranch ?? "" })
            .ToList();
    }

    /// <summary>
    /// The per-repository CONFLICTED blocks of the most recent <c>merge</c> whose multi-repo integration conflicted
    /// (resolver loop #379, S7-D2) — each conflicted repo's id + alias + the files that conflicted, the durable input
    /// the per-repo resolver recipe is assembled from. Empty when no prior merge has a multi-repo <c>repositories[]</c>
    /// block with a Conflicted repo (a single-repo conflict, or none). Walks newest-first; pure + best-effort.
    /// </summary>
    public static IReadOnlyList<SupervisorConflictedRepo> ReadConflictedRepos(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        for (var i = priorDecisions.Count - 1; i >= 0; i--)
        {
            var decision = priorDecisions[i];

            if (decision.DecisionKind != SupervisorDecisionKinds.Merge) continue;

            var conflicted = ReadConflictedReposFromMerge(decision.OutcomeJson);

            if (conflicted.Count > 0) return conflicted;
        }

        return Array.Empty<SupervisorConflictedRepo>();
    }

    /// <summary>
    /// Whether a <c>merge</c> outcome's integration block is MULTI-repo — it carries a per-repo <c>repositories[]</c>
    /// array (resolver loop #379, S7-D2). The routing key for resolution: a multi-repo conflict takes the per-repo
    /// resolver path EVEN when no repo is exactly "Conflicted" (e.g. it aggregated to Conflicted via a Failed repo) —
    /// so it never misroutes to the single-repo flat path. Best-effort + pure.
    /// </summary>
    public static bool HasPerRepoIntegration(string? outcomeJson)
    {
        if (string.IsNullOrWhiteSpace(outcomeJson)) return false;

        try
        {
            var root = JsonDocument.Parse(outcomeJson).RootElement;

            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("integration", out var integration) && integration.ValueKind == JsonValueKind.Object
                && integration.TryGetProperty("repositories", out var repositories) && repositories.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Read the Conflicted per-repo blocks off ONE multi-repo merge's <c>integration.repositories[]</c> — each with its repo id + alias + conflicted files (deduped, order-preserved). Empty for a single-repo merge or an all-clean block. Best-effort + pure.</summary>
    private static IReadOnlyList<SupervisorConflictedRepo> ReadConflictedReposFromMerge(string? outcomeJson)
    {
        if (string.IsNullOrWhiteSpace(outcomeJson)) return Array.Empty<SupervisorConflictedRepo>();

        try
        {
            var root = JsonDocument.Parse(outcomeJson).RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("integration", out var integration) || integration.ValueKind != JsonValueKind.Object
                || !integration.TryGetProperty("repositories", out var repositories) || repositories.ValueKind != JsonValueKind.Array)
                return Array.Empty<SupervisorConflictedRepo>();

            var conflicted = new List<SupervisorConflictedRepo>();

            foreach (var repo in repositories.EnumerateArray())
            {
                if (repo.ValueKind != JsonValueKind.Object) continue;

                if (!(repo.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() == "Conflicted")) continue;

                var files = new List<string>();
                CollectOutcomeDetail(repo.TryGetProperty("outcomes", out var o) ? o : default, files, new List<string>());

                conflicted.Add(new SupervisorConflictedRepo
                {
                    RepositoryId = repo.TryGetProperty("repositoryId", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var g) ? g : null,
                    Alias = repo.TryGetProperty("alias", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "",
                    ConflictedFiles = files,
                });
            }

            return conflicted;
        }
        catch (JsonException)
        {
            return Array.Empty<SupervisorConflictedRepo>();
        }
    }

    /// <summary>Read the folded compact agent results from a spawn/retry outcome (empty when absent/malformed/not-yet-folded). The decider sees these via the rendered outcome; a merge / scorecard can also read them.</summary>
    public static IReadOnlyList<SupervisorAgentResult> ReadAgentResults(string? outcomeJson)
    {
        if (string.IsNullOrWhiteSpace(outcomeJson)) return Array.Empty<SupervisorAgentResult>();

        try
        {
            var root = JsonDocument.Parse(outcomeJson).RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("agentResults", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<SupervisorAgentResult>();

            return arr.Deserialize<List<SupervisorAgentResult>>(AgentJson.Options) ?? (IReadOnlyList<SupervisorAgentResult>)Array.Empty<SupervisorAgentResult>();
        }
        catch (JsonException)
        {
            return Array.Empty<SupervisorAgentResult>();
        }
    }

    /// <summary>Read the model-authored SEMANTIC PHASES off a <c>plan</c> decision's outcome (L4 arc C) — empty when the plan recorded no phases (a flat plan) or the json is malformed. Pure; the phase projection (C2) reads it.</summary>
    public static IReadOnlyList<SupervisorPlanPhase> ReadPlanPhases(string? planOutcomeJson)
    {
        if (string.IsNullOrWhiteSpace(planOutcomeJson)) return Array.Empty<SupervisorPlanPhase>();

        try
        {
            var root = JsonDocument.Parse(planOutcomeJson).RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("phases", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<SupervisorPlanPhase>();

            return arr.Deserialize<List<SupervisorPlanPhase>>(AgentJson.Options) ?? (IReadOnlyList<SupervisorPlanPhase>)Array.Empty<SupervisorPlanPhase>();
        }
        catch (JsonException)
        {
            return Array.Empty<SupervisorPlanPhase>();
        }
    }

    /// <summary>Read the model-authored planned SUBTASKS off a <c>plan</c> decision's PAYLOAD (loopability slice 1) — each carrying its optional <c>dependsOn</c> + <c>acceptance</c> contract. Empty when absent/malformed. The per-unit acceptance fold reads each spawned unit's subtask <c>Acceptance</c> through here (joined positionally to the agent that ran it). Pure + best-effort.</summary>
    public static IReadOnlyList<SupervisorPlannedSubtask> ReadPlanSubtasks(string? planPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(planPayloadJson)) return Array.Empty<SupervisorPlannedSubtask>();

        try
        {
            return JsonSerializer.Deserialize<SupervisorPlanPayload>(planPayloadJson, AgentJson.Options)?.Subtasks
                   ?? Array.Empty<SupervisorPlannedSubtask>();
        }
        catch (JsonException)
        {
            return Array.Empty<SupervisorPlannedSubtask>();
        }
    }

    /// <summary>Read the plan-local subtask ids off a <c>spawn</c> decision's PAYLOAD (the fan-out order) — empty when absent/malformed. The spawn outcome's <c>agentRunIds[i]</c> corresponds to this payload's <c>subtaskIds[i]</c> (same staging order), so a phase's grouped subtasks map to the agents that ran them (C2).</summary>
    public static IReadOnlyList<string> ReadSpawnSubtaskIds(string? spawnPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(spawnPayloadJson)) return Array.Empty<string>();

        try
        {
            var root = JsonDocument.Parse(spawnPayloadJson).RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("subtaskIds", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            return arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Read the model-authored per-agent ROLE off a <c>spawn</c> decision's PAYLOAD <c>agents[]</c> (the <see cref="SupervisorAgentDispatch"/> specs) → <c>subtaskId → role</c>, only the entries that named a role; empty when absent/malformed (a homogeneous spawn omits <c>agents[]</c>). A NARROW projection — reads only the role leaf, never the repo/harness/autonomy fields (so the raw-JsonElement <c>targetRepos</c> never has to deserialize on this read path). Pure + best-effort, mirroring <see cref="ReadSpawnSubtaskIds"/>.</summary>
    public static IReadOnlyDictionary<string, string> ReadSpawnAgentRoles(string? spawnPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(spawnPayloadJson)) return EmptyRoles;

        try
        {
            var root = JsonDocument.Parse(spawnPayloadJson).RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("agents", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return EmptyRoles;

            var map = new Dictionary<string, string>();

            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                if (!e.TryGetProperty("subtaskId", out var sid) || sid.ValueKind != JsonValueKind.String) continue;
                if (!e.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String) continue;

                var roleStr = role.GetString();
                if (!string.IsNullOrWhiteSpace(roleStr)) map[sid.GetString()!] = roleStr!;
            }

            return map;
        }
        catch (JsonException)
        {
            return EmptyRoles;
        }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyRoles = new Dictionary<string, string>();

    /// <summary>Read the single plan-local subtask id off a <c>retry</c> decision's PAYLOAD — null when absent/malformed. A retry re-runs ONE subtask as a fresh agent; the phase projection appends that fresh agent to the subtask's attempt list (after the failed original), so the room renders BOTH the failure and its recovery.</summary>
    public static string? ReadRetrySubtaskId(string? retryPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(retryPayloadJson)) return null;

        try
        {
            var root = JsonDocument.Parse(retryPayloadJson).RootElement;

            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("subtaskId", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
