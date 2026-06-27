namespace CodeSpace.Messages.Tasks;

/// <summary>
/// The operator's optional EXECUTION overrides for a launched task (Rule 18.1, a pure data noun) — the harness /
/// model / persona / runner / credential the launch service folds into a <see cref="ResolvedAgentProfile"/>. Every
/// field is OPTIONAL and folds to the SAME default <c>agent.code</c> uses when absent (a null harness → the harness
/// default, etc.), so a bare task launches identically to an authored bare <c>agent.code</c> node. <see cref="Harness"/>
/// / <see cref="RunnerKind"/> are OPEN STRINGS (the registries resolve them).
///
/// <para>Safety-budget caps (MaxParallelism / MaxTotalSpawns / MaxCostUsd / MaxRounds) are NOT here — they are a
/// supervisor / cost concern carried by the sibling <see cref="TaskCapsOverride"/>, which rides the router's
/// <c>CapsOverride</c> seam. ApprovalPolicy rides the autonomy tier separately.</para>
/// </summary>
public sealed record TaskExecutionOverrides
{
    /// <summary>The harness the agent runs on (e.g. <c>"codex-cli"</c>). Null / blank → the projection's harness default.</summary>
    public string? Harness { get; init; }

    /// <summary>The model id within the harness's catalog. Null / blank → the persona's model → the harness default.</summary>
    public string? Model { get; init; }

    /// <summary>The Agent persona (<c>AgentDefinition</c>) the agent embodies. Null → a pure-inline run.</summary>
    public Guid? AgentDefinitionId { get; init; }

    /// <summary>The sandbox runner the agent executes on (e.g. <c>"local"</c>). Null → the executor's default.</summary>
    public string? RunnerKind { get; init; }

    /// <summary>The <c>ModelCredential</c> reference the agent authenticates with. Null → the persona default → the team/operator fallback.</summary>
    public Guid? ModelCredentialId { get; init; }

    /// <summary>A picked credentialed model (a <c>ModelCredentialModel</c> row) — sets BOTH the model and its backing credential from one choice, taking precedence over <see cref="Model"/> / <see cref="ModelCredentialId"/>. Null → those loose fields.</summary>
    public Guid? ModelCredentialModelId { get; init; }

    /// <summary>The agent run's wall-clock cap, in seconds. Null → the projection's bounded default (1h). 0 → NO wall-clock (unbounded — bounded only by the stall watchdog + cost cap). A positive value caps the run.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>Deep/supervisor only: per-run opt-in to INTEGRATING the spawned agents' diffs into one reviewable branch at merge (SOTA #3). Null / false → defer to the ambient integrate flag (byte-identical). Inert on a single-agent run.</summary>
    public bool? IntegrateBranches { get; init; }

    /// <summary>The working-directory mode in a MULTI-repo workspace, in wire vocabulary (<c>"workspace"</c> / <c>"primary"</c>; <c>"auto"</c> / null → the Auto default, omitted). Parsed by <c>WorkspaceCwdModeWire.FromWire</c> at profile build. Inert on a single-repo run.</summary>
    public string? CwdMode { get; init; }

    /// <summary>Per-run opt-in to the FULL MCP tool-fabric (the side-effecting catalog) for the agent. Null / false → defer to the ambient deployment flag (an ordinary run keeps the read-only catalog unless the deployment forces full) — byte-identical. The gate is OR-only: this can force the full fabric ON, it cannot turn it OFF when the deployment enabled it.</summary>
    public bool? EnableMcp { get; init; }
}
