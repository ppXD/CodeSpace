namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Resolves an <see cref="ISandboxRunner"/> by its <see cref="ISandboxRunner.Kind"/>. Same shape as
/// <c>ILLMClientRegistry</c> — the policy that decides which runner an agent run uses (config,
/// team tier, air-gapped override) lives in the caller; this registry only maps a kind to its
/// implementation. A new runner becomes resolvable by registering its class — no edit here.
/// </summary>
public interface ISandboxRunnerRegistry
{
    /// <summary>Every registered runner, for diagnostics / "which backends are available" surfaces.</summary>
    IReadOnlyList<ISandboxRunner> All { get; }

    /// <summary>Resolve the runner for <paramref name="kind"/>. Throws when none is registered for that kind.</summary>
    ISandboxRunner Resolve(string kind);
}
