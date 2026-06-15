using System.Text.Json;

namespace CodeSpace.Messages.Tasks.Phases;

/// <summary>
/// The small numeric roll-up a phase chip shows (e.g. "3 agents · 2 ✓ · 1 ✗"). <see cref="Extra"/> is an open
/// dictionary a future source can stuff source-specific counters into WITHOUT widening this record (Rule 7) — the
/// renderer ignores keys it doesn't know. Empty by default.
/// </summary>
public sealed record PhaseMetrics
{
    public int AgentCount { get; init; }
    public int SucceededCount { get; init; }
    public int FailedCount { get; init; }

    /// <summary>Source-specific extra counters keyed by an open string — the forward-compatible escape hatch (Rule 7).</summary>
    public IReadOnlyDictionary<string, JsonElement> Extra { get; init; } = EmptyExtra;

    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyExtra = new Dictionary<string, JsonElement>();
}
