using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMap;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMapDynamic;

/// <summary>
/// The <c>plan-map-dynamic</c> projection — the MODEL-AUTHORED sibling of <c>plan-map-synth</c>. It shares the
/// EXACT planner→map→agent→synth→done skeleton (inherited from <see cref="PlanMapBuilderBase"/>, so the two
/// cannot drift), specializing ONLY the four points where the model-authored shape differs:
///
/// <para>
///   • the body agent's goal binds from <c>{{item.instruction}}</c> (the authored plan item's work statement);
///   • the body agent's mode binds from <c>{{item.kind}}</c> — the plan.author item's OPEN kind (e.g. research /
///     code); the node maps a recognised kind to permissions (research = read-only + no branch; code = workspace
///     write + push its own branch) UNDER the autonomy-tier + explicit-override precedence, so the
///     autonomy-ceiling clamp still bounds it and the MODEL decides each agent's intent instead of the builder
///     hardcoding it.
/// </para>
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

    /// <summary>The body agent's mode — the plan item's OPEN <c>kind</c> (e.g. "research" / "code"); the node maps a recognised kind to permissions + push under the autonomy-tier + override precedence and any other value to its safe default, never throwing.</summary>
    protected override string? BranchMode => "{{item.kind}}";
}
