using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Constants;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Rule-8 drift pin, tied to the BUILDERS — not just the constants. The reserved sets
/// (<see cref="WorkflowOutputKeys.Map"/> / <see cref="WorkflowOutputKeys.Loop"/>) only protect an author's
/// configurable name (a map <c>resultKey</c> / a loop var) from silent overwrite if they list EVERY key the
/// engine reducer writes. The two literal-pin tests in <c>DefinitionValidatorContainerHardeningTests</c> pin
/// the constant string VALUES, but a new key emitted by a builder yet left out of the aggregate list would
/// re-open the silent-overwrite bug while those literal pins still pass.
///
/// <para>So this pins the OTHER direction: drive the real reducers (<c>BuildMapOutputs</c> /
/// <c>BuildLoopOutputs</c> + <c>BuildLoopScope</c>, reached via InternalsVisibleTo) with sentinel
/// author-configurable keys, subtract those sentinels, and assert the REMAINDER equals the reserved set.
/// A new key added to a builder but forgotten in <c>WorkflowOutputKeys.Map</c>/<c>.Loop</c> fails here —
/// making the omission a test-visible decision.</para>
/// </summary>
[Trait("Category", "Unit")]
public class ReducerEmittedKeysPinTests
{
    [Fact]
    public void BuildMapOutputs_emits_exactly_the_resultKey_plus_the_reserved_map_keys()
    {
        const string resultKey = "results";   // the author-configurable key (not reserved)

        var emitted = WorkflowEngine.BuildMapOutputs(resultKey, new List<JsonElement>(), failed: 0).Keys.ToHashSet();

        emitted.Remove(resultKey).ShouldBeTrue("the reducer must write the array under the configured resultKey");
        emitted.ShouldBe(WorkflowOutputKeys.Map, ignoreOrder: true);   // every OTHER key the reducer writes is reserved
    }

    [Fact]
    public void BuildLoopOutputs_and_BuildLoopScope_emit_exactly_the_loop_vars_plus_the_reserved_loop_keys()
    {
        const string loopVarName = "acc";   // the author-configurable loop var (not reserved)
        var loopVars = new Dictionary<string, JsonElement> { [loopVarName] = JsonSerializer.SerializeToElement(0) };

        // BuildLoopOutputs spreads the loop vars then writes the output keys; BuildLoopScope injects the
        // iteration-scope keys into loop.* — union both so a NEW key on either path is caught.
        var outputKeys = WorkflowEngine.BuildLoopOutputs(loopVars, iterations: 0, failedIterations: 0, reason: "maxIterations").Keys;
        var scopeKeys = WorkflowEngine.BuildLoopScope(MinimalScope(), Array.Empty<KeyValuePair<string, IReadOnlyDictionary<string, JsonElement>>>(), loopVars, index: 0).Loop!.Keys;

        var emitted = outputKeys.Concat(scopeKeys).ToHashSet();

        emitted.Remove(loopVarName).ShouldBeTrue("the reducer + scope must carry the author's loop var");
        emitted.ShouldBe(WorkflowOutputKeys.Loop, ignoreOrder: true);   // every OTHER key is reserved
    }

    private static NodeRunScope MinimalScope() => new() { Trigger = new Dictionary<string, JsonElement>() };
}
