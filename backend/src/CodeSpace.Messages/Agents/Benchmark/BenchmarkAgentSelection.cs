namespace CodeSpace.Messages.Agents.Benchmark;

/// <summary>
/// The run-time agent SELECTION for a benchmark run (a pure data noun, Rule 18.1): WHICH agent attempts the corpus —
/// the harness binary it drives, the model it runs, the gateway credential it authenticates with, and how trusted it
/// runs. Every field is optional and folds to the corpus's deterministic default: a <c>null</c> selection (or all-null
/// fields) reproduces the pre-existing behaviour — the task's own <see cref="BenchmarkTask.Harness"/>, no model, no
/// credential, <see cref="AgentAutonomyLevel.Standard"/> autonomy — i.e. the environment's fake CLI with no gateway
/// auth (the deterministic CI plumbing). A real-model gate supplies a real harness (<c>claude-code</c>), the model id,
/// the seeded <see cref="ModelCredentialId"/>, and <see cref="AgentAutonomyLevel.Trusted"/> so a LIVE agent
/// authenticates to the gateway (the model API call needs network when the run is confined) and may edit the workspace
/// to actually solve the task.
///
/// <para>The selection lives on the RUN, not the task: a benchmark TASK is model-agnostic ("fix this bug"); the agent
/// that attempts it is the variable being benchmarked, so the SAME corpus runs under a fake CLI in CI and a real
/// claude on demand without touching any task definition.</para>
/// </summary>
public sealed record BenchmarkAgentSelection
{
    /// <summary>Override the harness binary the agent drives (e.g. <c>"claude-code"</c>). Null ⇒ the task's own <see cref="BenchmarkTask.Harness"/>.</summary>
    public string? Harness { get; init; }

    /// <summary>The model id the agent runs (the gateway's model). Null ⇒ unset (the harness/CLI default).</summary>
    public string? Model { get; init; }

    /// <summary>The seeded gateway <c>ModelCredential</c> the agent authenticates with — the executor resolves + projects it onto the harness env. Null ⇒ no credential (the fake CLI needs none).</summary>
    public Guid? ModelCredentialId { get; init; }

    /// <summary>How trusted the agent runs. Null ⇒ <see cref="AgentAutonomyLevel.Standard"/> (workspace-write, the corpus default). A real coding agent that must reach the gateway + edit to solve uses <see cref="AgentAutonomyLevel.Trusted"/>.</summary>
    public AgentAutonomyLevel? Autonomy { get; init; }
}
