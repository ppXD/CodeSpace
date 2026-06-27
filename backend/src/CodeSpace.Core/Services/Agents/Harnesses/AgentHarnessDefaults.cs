using CodeSpace.Core.Services.Agents.Harnesses.Codex;

namespace CodeSpace.Core.Services.Agents.Harnesses;

/// <summary>
/// The platform DEFAULT harness — the SINGLE source of truth (Rule 8) for "what harness runs when neither the operator
/// nor the model authored one". It is consumed wherever a harness must be stamped but none was chosen: the single-agent /
/// map projection (<c>AgentNodeMapping</c>), the supervisor spawn (<c>RealSupervisorActionExecutor</c>), and the
/// map-fanout planner projector (<c>WorkflowPlanProjector</c>). Previously this default was a const duplicated across
/// those sites — one of them a raw <c>"codex-cli"</c> literal rather than the shared const — so the default could only be
/// changed by editing every copy. Unified here so it changes in ONE place.
///
/// <para>Operator override (Rule 8 env escape hatch): set <see cref="DefaultHarnessEnvVar"/> to a registered harness kind
/// (e.g. <c>claude-code</c>) to flip the global default OFF codex — for an air-gapped / fork operator whose fleet should
/// default to a different CLI. Unset (or blank) → <c>codex-cli</c>, the safe floor, byte-identical to the prior hardcoded
/// behaviour. A bad / unregistered override is caught at STARTUP: the harness registry calls <see cref="Validate"/> on
/// construction and FAIL-FASTS, so a typo surfaces immediately rather than as a per-run harness-resolution failure on the
/// unclamped default paths. (This is the trusted-config counterpart to the planner's graceful clamp of a
/// model-hallucinated per-subtask harness.)</para>
/// </summary>
public static class AgentHarnessDefaults
{
    /// <summary>Env var naming the platform default harness when none is authored. Pinned by a test (Rule 8) — a rename is a prod-config change an air-gapped operator pinned, so it must be a compile-time-visible decision, not an invisible refactor.</summary>
    public const string DefaultHarnessEnvVar = "CODESPACE_DEFAULT_HARNESS";

    /// <summary>The default harness kind: the <see cref="DefaultHarnessEnvVar"/> override (trimmed) when set to a non-blank value, else <c>codex-cli</c> (<see cref="CodexHarness.HarnessKind"/>) — the safe floor.</summary>
    public static string DefaultHarness =>
        Environment.GetEnvironmentVariable(DefaultHarnessEnvVar) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : CodexHarness.HarnessKind;

    /// <summary>
    /// FAIL-FAST guard (called once at startup from the harness registry): if the operator set
    /// <see cref="DefaultHarnessEnvVar"/> to a kind NOT in <paramref name="registeredKinds"/>, throw — so a TYPO in a
    /// deliberate global config surfaces immediately, not as a per-run "no harness registered for kind 'X'" failure on
    /// the unclamped default paths (the single-agent / map projection + the supervisor spawn). This is the trusted-config
    /// counterpart to the planner's GRACEFUL clamp: a model-hallucinated harness is silently floored to a registered
    /// kind, but a deliberate operator override is loud about a typo. Unset / blank / already-registered → a no-op.
    /// </summary>
    public static void Validate(IReadOnlyCollection<string> registeredKinds)
    {
        var raw = Environment.GetEnvironmentVariable(DefaultHarnessEnvVar);

        if (string.IsNullOrWhiteSpace(raw)) return;

        var kind = raw.Trim();

        if (!registeredKinds.Any(k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{DefaultHarnessEnvVar}='{kind}' is not a registered harness kind (registered: {string.Join(", ", registeredKinds)}). Unset it or set it to a registered kind.");
    }
}
