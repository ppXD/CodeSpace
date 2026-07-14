using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// The COMPACT, decider-visible result of ONE agent a supervisor spawned (SOTA #2). Folded into a terminal
/// spawn/retry decision's recorded outcome at rehydrate so the supervisor decider can PERCEIVE what each of its
/// agents produced — the prerequisite for adaptively retrying a failed subtask instead of merging blindly. A
/// pure data noun (Rule 18.1): a projection of the agent's terminal <c>AgentRunResult</c> (the normalized harness
/// output) MINUS the unbounded fields — no patch, no transcript — so it stays token-cheap in the decider prompt
/// and is a pure function of immutable post-terminal state (no artifact-store resolve, replay-deterministic).
///
/// <para>Built by <c>SupervisorOutcome.ProjectCompact</c>, the single shared projector the rehydrate fold AND
/// the <c>merge</c> executor both consume, so the decider's view and the merge's view can never drift on which
/// fields an agent exposes.</para>
/// </summary>
public sealed record SupervisorAgentResult
{
    /// <summary>The spawned agent run's id (the join key back to the durable AgentRun row + the spawn outcome's agentRunIds).</summary>
    public required Guid AgentRunId { get; init; }

    /// <summary>The agent run's terminal ROW status name (e.g. "Succeeded" / "Failed" / "Cancelled" / "TimedOut") — authoritative, taken from the AgentRun row, so it is present even when the run never wrote a result (a cancelled/abandoned agent).</summary>
    public required string Status { get; init; }

    /// <summary>P3a-1: the CAS id of the acceptance oracle's captured output for THIS unit (stamped by the fold beside the verdict; null pre-P3a or when capture failed). The composer maps it onto the receipt's <c>EvidenceRef</c>.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? AcceptanceEvidenceId { get; init; }

    /// <summary>The agent's final summary message (null when it produced none).</summary>
    public string? Summary { get; init; }

    /// <summary>The failure detail when the agent failed — taken from the result's error, else the ROW error (a cancelled/abandoned agent sets the row error with no result). Null on success.</summary>
    public string? Error { get; init; }

    /// <summary>The git ground-truth repo-relative paths the agent changed (never the diff body). Defaults to empty and NEVER serializes null, so a consumer can always treat it as an array.</summary>
    public IReadOnlyList<string> ChangedFiles { get; init; } = Array.Empty<string>();

    /// <summary>The branch the agent's sandbox pushed (the PR-open handoff), null when it pushed none. In a MULTI-repo run this mirrors the PRIMARY repo's branch (the legacy compat field); the full per-repo set is in <see cref="RepositoryResults"/>.</summary>
    public string? ProducedBranch { get; init; }

    /// <summary>
    /// The agent's PER-REPOSITORY outcomes for a MULTI-repo workspace run (resolver loop #379 S7-B) — the compact
    /// projection of <c>AgentRunResult.RepositoryResults</c>, one entry per WRITABLE repo (alias + repository id +
    /// the branch it pushed + base). EMPTY for a single-repo run, whose single outcome is the top-level
    /// <see cref="ChangedFiles"/>/<see cref="ProducedBranch"/> — so a single-repo decision's folded agentResults are
    /// behaviour-identical (every consumer reads the same top-level fields). Surfaced INLINE in the durable
    /// <c>agentResults</c> array (like <see cref="ProducedBranch"/>) so the resolver loop's per-repo branch collection
    /// reads it straight off the ledger — replay-deterministic, no DB round-trip (S7-D). Defaults to empty and NEVER
    /// serializes null, so a consumer can always treat it as an array. Bounded (one entry per repo, paths not diffs),
    /// so it stays token-cheap; the decider prompt renders it field-selectively, never the raw array.
    /// </summary>
    public IReadOnlyList<RepositoryRunResult> RepositoryResults { get; init; } = Array.Empty<RepositoryRunResult>();

    /// <summary>Input (prompt) tokens the agent consumed (0 when its harness reported none). Rides INLINE in the durable agentResults array so the supervisor's cost bound (SOTA #4) sums realized spend straight off the ledger — no new query, replay-deterministic. Defaults 0 so an outcome folded before this field existed contributes 0 (fail-open back-compat).</summary>
    public int InputTokens { get; init; }

    /// <summary>Output (completion) tokens the agent produced (0 when its harness reported none). See <see cref="InputTokens"/>.</summary>
    public int OutputTokens { get; init; }

    /// <summary>The model the agent ran on (from its <c>AgentTask</c>), used to PRICE <see cref="InputTokens"/>/<see cref="OutputTokens"/>. Null/blank/unknown → unpriceable → the run contributes 0 to summed cost (fail-open).</summary>
    public string? Model { get; init; }

    /// <summary>
    /// The per-UNIT OBJECTIVE acceptance verdict (loopability slice 3): <c>true</c> = this unit's own planned-subtask
    /// acceptance command ran GREEN against the branch this agent produced; <c>false</c> = it FAILED (the unit's branch
    /// is NOT mergeable — the precise retry target, AND it is discounted from the no-progress evidence so a
    /// branch-pushed-but-rejected wave can't fake progress); <c>null</c> = the subtask authored no acceptance (or the
    /// grade was deferred — a multi-repo agent), so the prior self-report behaviour stands. Null-omitted
    /// (<c>[JsonIgnore(WhenWritingNull)]</c>) so an UNGRADED unit serializes byte-identical to before (the durable
    /// agentResults bytes are unchanged), and — folded post-terminal — it never enters the idempotency key.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AcceptancePassed { get; init; }

    /// <summary>S3 — whether the unit's BASE tree (the S1 immutable base it started from) passed the SAME oracle with no candidate work applied, captured beside the candidate grade so a differential consumer (V0+) can tell "the candidate broke it" from "it was already broken" and credit "the candidate fixed it". Null-omitted: only present when the fold captured a baseline (a contract-bearing single-repo unit whose manifest records its base and whose candidate grade actually ran).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? BaselinePassed { get; init; }

    /// <summary>The baseline grade's detail. "Unknown" has TWO encodings a consumer must fold together: a null <see cref="BaselinePassed"/> (never captured), and a false whose detail classifies as infra via <c>AgentAcceptanceContract.IsInfraFailure</c> (captured but unmeasurable — clone/setup/timeout faults; never read it as "was already broken") — F0's typed dispositions replace this convention. Null-omitted alongside <see cref="BaselinePassed"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? BaselineDetail { get; init; }

    /// <summary>The per-unit acceptance verdict detail (the grader's reason — e.g. "tests-passed", "tests-failed-exit-1", "no-branch-or-repo", "grade-error: …"). Null-omitted; only present alongside a non-null <see cref="AcceptancePassed"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AcceptanceDetail { get; init; }

    /// <summary>
    /// Whether this unit's self-report (<see cref="Status"/>) CONTRADICTS its objective grade (<see cref="AcceptancePassed"/>)
    /// — a <c>CodeSpace.Core.Services.Agents.AgentContradiction</c> value (P4-1): <c>over_claim</c> when
    /// <see cref="Status"/> is "Succeeded" but the check FAILED (the agent believes it's done; the check disagrees);
    /// <c>under_claim</c> when <see cref="Status"/> is "Failed" but the check PASSED (the agent gave up on work that
    /// was actually fine); null when there's nothing to compare (no grade) or the two agree. Folded ONCE, at the
    /// same instant <see cref="AcceptancePassed"/> is folded — never re-derived ad-hoc by a prompt renderer, so no
    /// two consumers can frame the same row differently. Null-omitted, mirroring <see cref="AcceptancePassed"/>'s
    /// back-compat contract.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Contradiction { get; init; }
}
