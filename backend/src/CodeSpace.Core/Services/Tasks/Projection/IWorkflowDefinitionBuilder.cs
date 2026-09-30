using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Tasks.Projection;

/// <summary>
/// Projects a <see cref="TaskBuildContext"/> into a <see cref="WorkflowDefinition"/> for one projection
/// strategy — the polymorphic seam the task layer fans out over. Each implementation owns ONE
/// <see cref="ProjectionKind"/> (an open string, e.g. <c>"single-agent"</c>) and self-registers via the
/// <c>ISingletonDependency</c> marker, so a new strategy is a new impl folder under
/// <c>Projection/Builders/&lt;Kind&gt;/</c> with ZERO edit to the registry / factory / core (Rule 18.3 +
/// Rule 7 — new strategies are sibling impls, never a wider interface).
///
/// <para>The build is a pure function of its context (Rule 16 — no DB, no router): same context → same
/// definition. The output MUST pass <c>DefinitionValidator</c> — the same always-valid contract
/// <c>IWorkflowPlanProjector.Project</c> holds, so the snapshot starter can run it unconditionally.</para>
/// </summary>
public interface IWorkflowDefinitionBuilder
{
    /// <summary>The projection kind this builder handles — the open string the registry indexes + resolves it by. Mirrors <c>IAgentHarness.Kind</c> / <c>ISandboxRunner.Kind</c>.</summary>
    string ProjectionKind { get; }

    /// <summary>The operator-command adapter actually emitted by Build. An unadvertised adapter stays unknown; it is never assumed executable from a projection name.</summary>
    TaskProjectionAcceptanceContract OperatorAcceptance => new(null, null);

    /// <summary>Build the (always-valid) workflow definition for <paramref name="context"/>. Throws nothing for a well-formed context; the output is guaranteed to pass <c>DefinitionValidator</c>.</summary>
    WorkflowDefinition Build(TaskBuildContext context);
}

/// <summary>
/// The operator-command adapter a builder emits. <paramref name="RequiresRepository"/> says whether it can grade an argv
/// floor only against a repository: the supervisor grades its terminal stop against the branch it publishes, so a
/// repository-free run leaves the floor ungraded, while a single agent's own oracle runs in whatever workspace the agent
/// ran in, a repository-free scratch one included. Defaults to requiring one, so an adapter that never says so is never
/// trusted to grade a floor it may not be able to run.
/// </summary>
public sealed record TaskProjectionAcceptanceContract(bool? AcceptsCommand, BenchmarkGradingKind? GradingKind, bool RequiresRepository = true)
{
    /// <summary>Whether this adapter grades an operator argv floor in a workspace that does, or does not, bind a repository — the one test the floor's disposition, the tier's mandate and an auto route's floor rule all apply.</summary>
    public bool GradesCommand(bool hasRepository) => AcceptsCommand == true && GradingKind == BenchmarkGradingKind.TestsPass && (hasRepository || !RequiresRepository);
}
