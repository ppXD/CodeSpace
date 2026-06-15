namespace CodeSpace.Messages.Tasks;

/// <summary>
/// The operator's optional EXECUTION overrides for a launched task (Rule 18.1, a pure data noun) — the harness /
/// model / persona / runner / credential the launch service folds into a <see cref="ResolvedAgentProfile"/>. Every
/// field is OPTIONAL and folds to the SAME default <c>agent.code</c> uses when absent (a null harness → the harness
/// default, etc.), so a bare task launches identically to an authored bare <c>agent.code</c> node. <see cref="Harness"/>
/// / <see cref="RunnerKind"/> are OPEN STRINGS (the registries resolve them).
///
/// <para>PR4-minimal: caps + approval-policy inputs (MaxParallelism / MaxTotalSpawns / MaxCostUsd / ApprovalPolicy)
/// are intentionally NOT modelled here — they are supervisor / cost concerns a later PR adds via the router's
/// <c>CapsOverride</c> seam, which PR4 leaves unused.</para>
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
}
