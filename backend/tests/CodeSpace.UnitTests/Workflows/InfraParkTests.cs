using System.Text.Json;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: pins A2's generic model-plane park — the ladder the supervisor has ridden since P1.1, now available to
/// any node that calls a model. The planner and synthesizer had NO retry policy and NO park, so the same provider
/// blip a Deep run sleeps through killed a Standard run in minutes.
///
/// <para>What these pin: the fault classes worth parking for (and the ones that must stay fail-fast), the ladder
/// continuing across wakes from its OWN marker only, the honest failure once the window is spent, the
/// iteration-key choice that keeps a parked map-branch node inside its own branch cell, and the text the park
/// writes down about the fault — redacted of scope secrets first, then clamped.</para>
/// </summary>
[Trait("Category", "Unit")]
public class InfraParkTests
{
    private static NodeRunContext Context(JsonElement? resumePayload = null) => new()
    {
        Inputs = new Dictionary<string, JsonElement>(),
        Config = new Dictionary<string, JsonElement>(),
        RawInputs = JsonDocument.Parse("{}").RootElement,
        RawConfig = JsonDocument.Parse("{}").RootElement,
        Scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement>(), Sys = new Dictionary<string, JsonElement>() },
        Logger = NullLogger.Instance,
        Observability = NodeObservability.NoOp,
        NodeId = "synth",
        ResumePayload = resumePayload,
    };

    private static LlmApiException Fault(LlmErrorCategory category) => new("Anthropic", 503, category, "upstream unavailable");

    private static LlmApiException FaultWith(string providerMessage) => new("Anthropic", 500, LlmErrorCategory.Transient, providerMessage);

    /// <summary>A value the run treats as secret — a team variable, listed on <c>SecretPaths</c> under the engine's own spelling (<c>&lt;bucket&gt;.&lt;variable&gt;</c>).</summary>
    private const string GatewayToken = "gw-tok-7f3a9c2e41d8";

    private static NodeRunContext ContextHoldingSecret() => Context() with
    {
        Scope = new NodeRunScope
        {
            Trigger = new Dictionary<string, JsonElement>(),
            Sys = new Dictionary<string, JsonElement>(),
            Team = new Dictionary<string, JsonElement> { ["GATEWAY_TOKEN"] = JsonSerializer.SerializeToElement(GatewayToken) },
            SecretPaths = new HashSet<string> { "team.GATEWAY_TOKEN" },
        },
    };

    /// <summary>The fault's text as each place the park writes it shows it: the marker's <c>error</c> on a park, that park's log line, and the honest failure once the whole window is spent.</summary>
    private static Dictionary<string, string> TextsWritten(NodeRunContext context, LlmApiException fault)
    {
        var logger = new CapturingLogger();
        var now = DateTimeOffset.UtcNow;

        var parked = InfraPark.Park(context with { Logger = logger }, fault, now);
        var failed = InfraPark.Park(context with { ResumePayload = parked.SuspendUntil!.TimeoutPayload }, fault, now + SupervisorInfraPark.MaxParkWindow);

        return new Dictionary<string, string>
        {
            ["marker"] = parked.SuspendUntil.Payload.GetProperty("error").GetString()!,
            ["log"] = logger.Messages.ShouldHaveSingleItem(),
            ["failure"] = failed.Error!,
        };
    }

    // ── Which faults park, and which must never ──────────────────────────────────────

    [Theory]
    [InlineData(LlmErrorCategory.Transient, true)]
    [InlineData(LlmErrorCategory.RateLimited, true)]
    [InlineData(LlmErrorCategory.AuthFailed, false)]
    public void Only_a_genuinely_transient_class_is_worth_parking_for(LlmErrorCategory category, bool parkable)
    {
        // An auth failure is operator-actionable NOW; parking one would hide it behind a 24h ladder.
        InfraPark.IsParkable(Fault(category)).ShouldBe(parkable);
    }

    // ── The first park ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_first_fault_parks_on_the_shared_wait_kind_with_a_deadline_wake()
    {
        var result = InfraPark.Park(Context(), Fault(LlmErrorCategory.Transient), DateTimeOffset.UtcNow);

        result.Status.ShouldBe(NodeStatus.Suspended, "a provider outage must not terminalize the run");
        result.SuspendUntil.ShouldNotBeNull();
        result.SuspendUntil!.Kind.ShouldBe(WorkflowWaitKinds.SupervisorInfraPark, "the SAME wait kind the supervisor uses — which is how the stranded-wait reconciler backstops this park with no new code");
        result.SuspendUntil.DeadlineAt.ShouldNotBeNull("the deadline IS the wake; nothing else resolves this wait");
        result.SuspendUntil.TimeoutPayload.ShouldNotBeNull("the wake must carry the ladder position forward");
    }

    [Fact]
    public void The_park_log_carries_the_faults_own_words()
    {
        // The planner parked on an empty-bodied gateway 500 run after run, and the park line named only the category —
        // so the one thing a reader needed, what the gateway actually said, was in no log line at all.
        var logger = new CapturingLogger();

        InfraPark.Park(Context() with { Logger = logger }, Fault(LlmErrorCategory.Transient), DateTimeOffset.UtcNow);

        logger.Messages.ShouldHaveSingleItem().ShouldContain("upstream unavailable");
    }

    // ── What the park writes down about the fault ────────────────────────────────────

    [Fact]
    public void A_scope_secret_in_the_faults_text_is_redacted_from_the_marker_the_log_and_the_failure()
    {
        // LlmApiException.Message ends with the provider's error body verbatim, and a gateway that rejects a request can
        // echo the credential it was sent. The marker is durable and shown on the run detail, the failure is the run's
        // own error and the log line leaves the process — none of the three may carry a value the run treats as secret.
        var fault = FaultWith($"gateway rejected bearer {GatewayToken} on /v1/messages");
        fault.Message.ShouldContain(GatewayToken, Case.Sensitive, "fixture check: the raw fault carries the secret, or the redaction asserted below proves nothing");

        foreach (var (place, text) in TextsWritten(ContextHoldingSecret(), fault))
        {
            text.ShouldNotContain(GatewayToken, Case.Sensitive, $"the {place} must never carry a scope secret");
            text.ShouldContain(PersistenceSecretRedactor.Marker, Case.Sensitive, $"the {place} says a value was withheld rather than silently losing words");
        }
    }

    [Fact]
    public void A_provider_body_of_thousands_of_characters_is_clamped_in_the_marker_the_log_and_the_failure()
    {
        // The same Message tail carries a gateway's WHOLE answer — an HTML page, a stack trace — onto the run row and
        // into the log line. 512 characters of it and the ellipsis is what a reader needs; the rest is noise at rest.
        var fault = FaultWith(new string('x', 5_000));
        var clamped = fault.Message[..512] + "…";

        var written = TextsWritten(Context(), fault);

        written["marker"].ShouldBe(clamped);
        written["log"].ShouldEndWith(clamped, Case.Sensitive);
        written["failure"].ShouldEndWith(clamped, Case.Sensitive);
    }

    [Fact]
    public void A_secret_that_straddles_the_clamp_is_redacted_whole_and_never_left_as_a_fragment()
    {
        // Redaction runs BEFORE the clamp. Clamped first, the cut would land inside the token and leave its opening
        // characters behind — a fragment no exact-value redactor would ever recognise.
        var fault = FaultWith(new string('x', 512 - FaultWith("").Message.Length - 4) + GatewayToken + new string('y', 200));
        fault.Message.IndexOf(GatewayToken, StringComparison.Ordinal).ShouldBe(508, "fixture check: the token starts four characters before the 512-character cut");

        foreach (var (place, text) in TextsWritten(ContextHoldingSecret(), fault))
            text.ShouldNotContain(GatewayToken[..4], Case.Sensitive, $"the {place} kept the opening of a secret that the clamp cut in half");
    }

    [Fact]
    public void The_clamp_never_cuts_a_surrogate_pair_in_half()
    {
        // A lone surrogate is ill-formed UTF-16. System.Text.Json quietly swaps it for U+FFFD on the marker, but the
        // failure text reaches the run row through Npgsql, whose strict UTF-8 encoder throws on it — so an emoji that
        // straddles the cut goes whole or not at all.
        var fault = FaultWith(new string('x', 511 - FaultWith("").Message.Length) + "\U0001F600tail");
        char.IsHighSurrogate(fault.Message[511]).ShouldBeTrue("fixture check: the 512-character cut would fall between the emoji's two halves");
        var clamped = fault.Message[..511] + "…";

        var written = TextsWritten(Context(), fault);

        written["marker"].ShouldBe(clamped);
        written["log"].ShouldEndWith(clamped, Case.Sensitive);
        written["failure"].ShouldEndWith(clamped, Case.Sensitive);
    }

    [Fact]
    public void The_park_keeps_the_nodes_ambient_cell_so_a_map_branch_stays_in_its_branch()
    {
        // The supervisor overrides IterationKey because its node is top-level. A generic node must NOT: the engine
        // falls back to the ambient cell key, which for a node inside a fan-out is its own branch.
        InfraPark.Park(Context(), Fault(LlmErrorCategory.Transient), DateTimeOffset.UtcNow)
            .SuspendUntil!.IterationKey.ShouldBeNullOrEmpty();
    }

    // ── The ladder across wakes ──────────────────────────────────────────────────────

    [Fact]
    public void A_wake_that_faults_again_advances_the_ladder_from_its_own_marker()
    {
        var now = DateTimeOffset.UtcNow;
        var first = InfraPark.Park(Context(), Fault(LlmErrorCategory.Transient), now);

        var second = InfraPark.Park(Context(first.SuspendUntil!.TimeoutPayload), Fault(LlmErrorCategory.Transient), now.AddMinutes(1));

        var parks = second.SuspendUntil!.TimeoutPayload!.Value.GetProperty("parks").GetInt32();
        parks.ShouldBe(2, "the ladder position rides the marker, so the second park waits longer than the first");
        second.SuspendUntil.TimeoutPayload.Value.GetProperty("firstParkedAtUtc").GetString()
            .ShouldBe(first.SuspendUntil!.TimeoutPayload!.Value.GetProperty("firstParkedAtUtc").GetString(), "the 24h window is anchored at the FIRST park, so a run can never park forever");
    }

    [Fact]
    public void A_non_park_resume_payload_starts_the_ladder_fresh()
    {
        // Load-bearing: a node resuming from a human answer / an agent barrier / a self-advance must not inherit
        // some other mechanism's park count. The marker read is self-identifying for exactly this reason.
        var foreign = JsonSerializer.SerializeToElement(new { answeredBy = "someone", parks = 3 });

        InfraPark.Park(Context(foreign), Fault(LlmErrorCategory.Transient), DateTimeOffset.UtcNow)
            .SuspendUntil!.TimeoutPayload!.Value.GetProperty("parks").GetInt32().ShouldBe(1);
    }

    // ── The honest ending ────────────────────────────────────────────────────────────

    [Fact]
    public void Past_the_whole_window_the_node_fails_honestly_instead_of_parking_forever()
    {
        var now = DateTimeOffset.UtcNow;
        var stale = InfraPark.Park(Context(), Fault(LlmErrorCategory.Transient), now).SuspendUntil!.TimeoutPayload;

        var result = InfraPark.Park(Context(stale), Fault(LlmErrorCategory.Transient), now + SupervisorInfraPark.MaxParkWindow);

        result.Status.ShouldBe(NodeStatus.Failure, "a 24h outage is a real failure — parking past the window would hide an outage nobody is coming to fix");
        result.Error.ShouldContain("model plane", Case.Insensitive);
        result.Retryable.ShouldBeFalse("re-running the node cannot reach a provider that has been down for a day");
    }

    /// <summary>Keeps every line the park writes, formatted the way a sink would render it.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }
}
