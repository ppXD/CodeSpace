using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMap;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMapSynth;

/// <summary>
/// The <c>plan-map-synth</c> projection (Rule 18.3 — one impl beside its variant folder): the FIRST multi-agent
/// task shape. The triad's <c>plan.author</c> planner decomposes the task into a DURABLE flat plan, a
/// <c>flow.map</c> fans the subtasks out over one real <c>agent.run</c> body per item, and a synthesizer
/// reduces the per-branch results into the run's output. It shares the planner→map→agent→synth→done skeleton
/// with its sibling <c>plan-map-dynamic</c> via <see cref="PlanMapBuilderBase"/>; this variant authors no
/// per-branch mode, so the body agent inherits the profile's posture (the historical behaviour).
///
/// <para><b>Build stays PURE</b> — the planner is a NODE that runs at EXECUTION, not a build-time LLM call — so the
/// output always passes the real <c>DefinitionValidator</c>. Self-registers via <see cref="ISingletonDependency"/>;
/// a new projection is a sibling builder folder (like plan-map-dynamic), never an edit here.</para>
/// </summary>
public sealed class PlanMapSynthDefinitionBuilder : PlanMapBuilderBase, ISingletonDependency
{
    public override string ProjectionKind => TaskProjectionKinds.PlanMapSynth;

    /// <summary>The body agent's goal — this branch's authored instruction (the plan item's concrete work statement).</summary>
    protected override string BranchGoal => "{{item.instruction}}";
}
