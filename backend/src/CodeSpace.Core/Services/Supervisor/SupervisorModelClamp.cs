namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The per-agent MODEL privilege gate (the model analogue of <see cref="SupervisorRepoClamp"/>): clamps a
/// model-authored per-agent model (a spawn dispatch's <c>model</c>) to the operator's ALLOWED MODEL POOL. The model
/// PROPOSES which model an agent runs on; the server GRANTS only a model the operator put in the pool. Pure +
/// name-based — the supervisor authors a model NAME (its backing credential comes from the operator's profile, never
/// the model), so the boundary is a simple name-membership check, with no static harness catalog to wrongly reject a
/// custom / gateway model. An EMPTY / null pool = NO restriction (opt-in): the supervisor's intelligence picks any
/// model, byte-identical to before. Fail-closed — throws <see cref="SupervisorModelAccessException"/> when a non-empty
/// pool does not contain the authored model.
/// </summary>
public static class SupervisorModelClamp
{
    /// <summary>
    /// Resolve + validate the effective model for a spawned agent. Precedence (unchanged): the model-authored
    /// <paramref name="requestedModel"/>, else the operator profile's <paramref name="profileModel"/>, else null (the
    /// harness default). When <paramref name="allowedModels"/> is non-empty, the effective NAMED model must be in it
    /// (case-insensitive) — else fail-closed. A null effective model (no name authored → the harness picks its own
    /// default) is NOT clamped: there is no name to gate, and the operator's pool bounds explicit choices, not the
    /// harness's own fallback. No pool → the effective model passes through (byte-identical).
    /// </summary>
    public static string? ClampModel(string? requestedModel, string? profileModel, IReadOnlyList<string>? allowedModels)
    {
        var effective = NullIfBlank(requestedModel) ?? NullIfBlank(profileModel);

        if (allowedModels is null || allowedModels.Count == 0) return effective;

        if (effective is null) return effective;

        if (!allowedModels.Any(m => string.Equals(m, effective, StringComparison.OrdinalIgnoreCase)))
            throw new SupervisorModelAccessException($"Agent dispatch requests model '{effective}', which the operator did not include in this run's allowed model pool.");

        return effective;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Raised when a model-authored agent dispatch requests a model the operator did not put in the run's allowed model pool — the per-agent model privilege gate's fail-closed signal.</summary>
public sealed class SupervisorModelAccessException : Exception
{
    public SupervisorModelAccessException(string message) : base(message) { }
}
