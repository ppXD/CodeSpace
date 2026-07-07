using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The pure schedule + marker logic behind the supervisor's MODEL-PLANE OUTAGE park (P1.1). When the brain call
/// exhausts its bounded in-call retry on a transient/rate-limit fault, the node no longer terminalizes the durable
/// run — it parks on a <c>SupervisorInfraPark</c> wait whose <c>DeadlineAt</c> walks this exponential ladder
/// (1m → 5m → 15m → 60m, ±20% jitter so a fleet of parked runs never re-storms a recovering provider in lockstep),
/// and whose <c>TimeoutPayload</c> carries <c>{ infraPark, parks, firstParkedAtUtc }</c> so the wake RE-ENTERS the
/// SAME turn with the ladder position intact. A wake that decides successfully simply continues the run (the marker
/// is naturally forgotten); a wake that faults again advances the ladder. Once the whole <see cref="MaxParkWindow"/>
/// has elapsed the run stops honestly (<c>SupervisorStopReasons.ModelPlaneUnavailable</c> — a degraded
/// <c>Stopped</c>, never a fake success). Pure statics — no clock, no DB — so every branch is unit-pinned.
/// </summary>
public static class SupervisorInfraPark
{
    /// <summary>The exponential park ladder; park N waits Schedule[min(N−1, last)] (±20% jitter). Pinned by a unit test (Rule 8).</summary>
    public static readonly TimeSpan[] Schedule = { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60) };

    /// <summary>The TOTAL outage window one run rides out before it stops honestly — measured from the FIRST park, so a run can never park forever. Pinned by a unit test (Rule 8).</summary>
    public static readonly TimeSpan MaxParkWindow = TimeSpan.FromHours(24);

    /// <summary>The self-identifying marker field on the park's resume payload — how a re-entry tells an infra-park wake from every other resume shape.</summary>
    public const string MarkerField = "infraPark";

    /// <summary>Whether a brain-call fault is PARKABLE — a genuinely transient infra class a later wake may outlive. AuthFailed and every model-side miss stay fail-fast: they are operator-actionable (or deterministic) NOW, and parking would only hide them.</summary>
    public static bool IsParkable(LlmErrorCategory category) => category is LlmErrorCategory.Transient or LlmErrorCategory.RateLimited;

    /// <summary>
    /// Fold the prior park state (the resume payload, when it carries the <see cref="MarkerField"/>) + <paramref name="now"/>
    /// into the NEXT park: parks = prior + 1, the window anchored at the FIRST park. <see cref="State.WindowExhausted"/>
    /// is true when the whole <see cref="MaxParkWindow"/> has already elapsed — the caller stops the run honestly
    /// instead of parking again.
    /// </summary>
    public static State Next(JsonElement? resumePayload, DateTimeOffset now)
    {
        var prior = Read(resumePayload);
        var first = prior?.FirstParkedAtUtc ?? now;

        return new State
        {
            Parks = (prior?.Parks ?? 0) + 1,
            FirstParkedAtUtc = first,
            WindowExhausted = now - first >= MaxParkWindow,
        };
    }

    /// <summary>The wait before <paramref name="parks"/>-th wake: the ladder rung (clamped to the last) with ±20% jitter. Zero/negative parks read the first rung (defensive).</summary>
    public static TimeSpan DelayFor(int parks)
    {
        var rung = Schedule[Math.Clamp(parks - 1, 0, Schedule.Length - 1)];

        return rung * (0.8 + Random.Shared.NextDouble() * 0.4);
    }

    /// <summary>The park marker — BOTH the suspend payload (what the run-detail shows while parked) and the wait's <c>TimeoutPayload</c> (what the deadline injects as the wake's resume payload), so the re-entry reads the ladder position durably.</summary>
    public static JsonElement Marker(State state, string error) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            [MarkerField] = true,
            ["parks"] = state.Parks,
            ["firstParkedAtUtc"] = state.FirstParkedAtUtc.ToString("o"),
            ["error"] = error,
        });

    /// <summary>Read a prior park's state off a resume payload — null when the payload is absent or not an infra-park marker (a self-advance marker, a human answer, an agent barrier: every other resume shape reads null, so a park ladder only ever continues from its OWN wakes).</summary>
    private static State? Read(JsonElement? resumePayload)
    {
        if (resumePayload is not { ValueKind: JsonValueKind.Object } payload) return null;

        if (!payload.TryGetProperty(MarkerField, out var marker) || marker.ValueKind != JsonValueKind.True) return null;

        var parks = payload.TryGetProperty("parks", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
        var first = payload.TryGetProperty("firstParkedAtUtc", out var f) && f.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(f.GetString(), out var parsed) ? parsed : (DateTimeOffset?)null;

        return first is null ? null : new State { Parks = parks, FirstParkedAtUtc = first.Value, WindowExhausted = false };
    }

    /// <summary>One park's durable position on the ladder (a data noun local to this concern).</summary>
    public sealed record State
    {
        /// <summary>How many parks this outage has taken INCLUDING the one being staged (1-based).</summary>
        public required int Parks { get; init; }

        /// <summary>When the FIRST park of this outage was staged — the anchor <see cref="MaxParkWindow"/> is measured from.</summary>
        public required DateTimeOffset FirstParkedAtUtc { get; init; }

        /// <summary>True when the whole park window has elapsed — stop honestly instead of parking again.</summary>
        public required bool WindowExhausted { get; init; }
    }
}
