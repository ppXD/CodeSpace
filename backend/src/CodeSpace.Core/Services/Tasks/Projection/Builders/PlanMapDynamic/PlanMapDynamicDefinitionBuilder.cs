using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMap;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMapDynamic;

/// <summary>
/// The <c>plan-map-dynamic</c> projection — the MODEL-AUTHORED sibling of <c>plan-map-synth</c>. It shares the
/// EXACT planner→map→agent→synth→done skeleton (inherited from <see cref="PlanMapBuilderBase"/>, so the two
/// cannot drift): the body agent's goal binds from <c>{{item.instruction}}</c> (the authored plan item's work
/// statement), and its mode from <c>{{item.kind}}</c>.
///
/// <para>The kind→mode binding now lives on the SHARED base — the planner types every plan on every variant, and
/// the posture it implies only ever LOWERS privilege, so the default lane reads it too. What is left here is the
/// RECIPE's opt-in identity: this projection is routed only by an explicit <c>map-fanout-dynamic</c> request.</para>
///
/// <para>Self-registers via <see cref="ISingletonDependency"/>; this is a NEW opt-in sibling projection, so
/// plan-map-synth stays byte-identical (the shared base is behaviour-preserving — both variants emit exactly what
/// they did before the extraction).</para>
/// </summary>
public sealed class PlanMapDynamicDefinitionBuilder : PlanMapBuilderBase, ISingletonDependency
{
    public override string ProjectionKind => TaskProjectionKinds.PlanMapDynamic;

    /// <summary>The body agent's goal — this branch's authored instruction (the plan item's concrete work statement).</summary>
    protected override string BranchGoal => "{{item.instruction}}";
}
