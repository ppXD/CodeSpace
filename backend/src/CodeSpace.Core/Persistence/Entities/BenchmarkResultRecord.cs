namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// A4: one durable row per benchmark (task × mode) cell the corpus runner ran. Until now the per-cell results
/// reached the CI step summary and nowhere else, so a solve rate was never comparable across runs, commits, or
/// model bundles.
///
/// <para>Append-only and observation-only: a re-run of the same cell is a NEW measurement (never a correction of
/// the old one), and the benchmark gate's verdict is still computed from the in-memory <c>CorpusBenchmarkRun</c> —
/// the runner swallows a write fault, so persistence can neither fail nor pass a gate.</para>
/// </summary>
public class BenchmarkResultRecord : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public Guid TeamId { get; set; }

    /// <summary>The corpus's identity: <c>EvalSuiteManifest.Version</c> — content-derived and algorithm-prefixed, the only stable name a corpus has.</summary>
    public string SuiteVersion { get; set; } = string.Empty;

    public string TaskId { get; set; } = string.Empty;

    /// <summary>The <c>BenchmarkMode</c> name the task ran through.</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>The harness the attempting agent used; null when the run used the deterministic fake CLI (no selection was passed).</summary>
    public string? Harness { get; set; }

    /// <summary>The model the attempting agent was pinned to; null under the fake CLI.</summary>
    public string? Model { get; set; }

    /// <summary>The agent run that executed the cell — provenance back to its event log. Null when the run was never created (a setup failure).</summary>
    public Guid? AgentRunId { get; set; }

    /// <summary>The OBJECTIVE grade: did the oracle judge the task solved. Distinct from <see cref="RunStatus"/> — a run can Succeed and fail the grade.</summary>
    public bool Solved { get; set; }

    /// <summary>The agent run's own terminal <c>AgentRunStatus</c> name.</summary>
    public string RunStatus { get; set; } = string.Empty;

    /// <summary>Bounded revise rounds the executor spent inside the cell — the retry share that keeps an A/B honest.</summary>
    public int ReviseRounds { get; set; }

    /// <summary>Whether the run-scoped MCP endpoint served the FULL tool catalog (the load-bearing difference between the two harness-CLI modes).</summary>
    public bool McpFullCatalog { get; set; }

    /// <summary>The run's terminal exit reason — scopes an intervention proxy to the critic-flag path (<c>output-flagged</c>). Null when the run recorded no result.</summary>
    public string? ExitReason { get; set; }

    /// <summary>Priced USD over the cell's billed tokens; null when the run reported no usage (the fake CLI emits none) or the model is unpriceable.</summary>
    public decimal? CostUsd { get; set; }

    public double? DurationSeconds { get; set; }

    /// <summary>The commit the harness ran from, when the process was given one (CI provenance). Null outside CI.</summary>
    public string? GitSha { get; set; }

    /// <summary>The CI run the measurement came from, when the process was given one. Null outside CI.</summary>
    public string? CiRunId { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
