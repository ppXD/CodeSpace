using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Contract tests: every built-in node's manifest declares non-empty TypeKey, sensible
/// Kind, and round-trippable JSON schemas. Catches the "I forgot to set IconKey on the
/// new node so the editor breaks" class of regression without needing to wire DI.
/// </summary>
[Trait("Category", "Unit")]
public class NodeManifestContractTests
{
    public static IEnumerable<object[]> AllPureNodes()
    {
        // Trigger + terminal nodes have no external deps; we can instantiate them directly.
        // git.* + llm.* take service deps via constructor, so contract-test them separately.
        yield return new object[] { new TriggerPrOpenedNode() };
        yield return new object[] { new TriggerPrUpdatedNode() };
        yield return new object[] { new TriggerManualNode() };
        yield return new object[] { new TerminalNode() };
    }

    [Theory]
    [MemberData(nameof(AllPureNodes))]
    public void TypeKey_is_non_empty(INodeRuntime node)
    {
        node.TypeKey.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [MemberData(nameof(AllPureNodes))]
    public void Manifest_has_displayname_and_category(INodeRuntime node)
    {
        node.Manifest.DisplayName.ShouldNotBeNullOrWhiteSpace();
        node.Manifest.Category.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [MemberData(nameof(AllPureNodes))]
    public void Schemas_are_objects(INodeRuntime node)
    {
        node.Manifest.ConfigSchema.ValueKind.ShouldBe(JsonValueKind.Object);
        node.Manifest.InputSchema.ValueKind.ShouldBe(JsonValueKind.Object);
        node.Manifest.OutputSchema.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public void Trigger_nodes_declare_trigger_kind()
    {
        new TriggerPrOpenedNode().Manifest.Kind.ShouldBe(NodeKind.Trigger);
        new TriggerPrUpdatedNode().Manifest.Kind.ShouldBe(NodeKind.Trigger);
        new TriggerManualNode().Manifest.Kind.ShouldBe(NodeKind.Trigger);
    }

    [Fact]
    public void Only_the_manual_trigger_declares_IsManual_true()
    {
        // IsManual drives deriveActivations: a manual trigger produces NO workflow_activation
        // row (nothing to match events against), event triggers DO. Pinning both directions
        // here makes a future flip (e.g. someone setting IsManual on a PR trigger, or dropping
        // it from the manual node) a loud, review-visible test failure rather than a silent
        // activation-model regression.
        new TriggerManualNode().Manifest.IsManual.ShouldBeTrue(
            "trigger.manual is on-demand and must declare IsManual=true so the editor skips its activation row");

        new TriggerPrOpenedNode().Manifest.IsManual.ShouldBeFalse(
            "event triggers subscribe to webhooks and MUST keep IsManual=false so deriveActivations still emits their activation");
        new TriggerPrUpdatedNode().Manifest.IsManual.ShouldBeFalse(
            "event triggers subscribe to webhooks and MUST keep IsManual=false so deriveActivations still emits their activation");
    }

    [Fact]
    public async Task Manual_trigger_echoes_run_payload_as_outputs()
    {
        // Mirrors the PR triggers: the trigger node copies scope.Trigger into its outputs so a
        // downstream node can read either {{trigger.x}} or {{nodes.<id>.outputs.x}}.
        var payload = JsonDocument.Parse("""{"ticket":"ABC-123"}""").RootElement
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

        var context = new NodeRunContext
        {
            Inputs = new Dictionary<string, JsonElement>(),
            Config = new Dictionary<string, JsonElement>(),
            RawInputs = JsonDocument.Parse("{}").RootElement,
            RawConfig = JsonDocument.Parse("{}").RootElement,
            Scope = new NodeRunScope { Trigger = payload },
            Logger = NullLogger.Instance,
            Observability = NodeObservability.NoOp,
        };

        var result = await new TriggerManualNode().RunAsync(context, CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        result.Outputs["ticket"].GetString().ShouldBe("ABC-123");
    }

    [Fact]
    public void Terminal_node_declares_terminal_kind()
    {
        new TerminalNode().Manifest.Kind.ShouldBe(NodeKind.Terminal);
    }

    [Fact]
    public void TypeKeys_match_dotted_naming_convention()
    {
        foreach (var node in AllPureNodes().Select(arr => (INodeRuntime)arr[0]))
        {
            node.TypeKey.ShouldMatch(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$",
                $"TypeKey '{node.TypeKey}' should be dotted lowercase identifiers (e.g. 'category.action').");
        }
    }

    [Theory]
    [MemberData(nameof(AllPureNodes))]
    public void Pure_nodes_declare_IsSideEffecting_false(INodeRuntime node)
    {
        // Trigger + terminal nodes are pure (no external side effects), so their
        // IsSideEffecting MUST be false. The engine's abandoned-run guard reads this. A new
        // node added to AllPureNodes() that mistakenly sets IsSideEffecting=true would fail
        // this assertion and force the author to either fix the marker OR move it to the
        // side-effecting list — making the decision explicit at review time.
        node.Manifest.IsSideEffecting.ShouldBeFalse(
            $"node '{node.TypeKey}' is classed as pure but its manifest declares IsSideEffecting=true; " +
            "either change the marker or move this node into the side-effecting contract test list.");
    }

    // ─── Side-effecting nodes' IsSideEffecting contract ─────────────────────────
    //
    // These nodes have external deps so we instantiate-with-null where the test only inspects
    // the manifest (which is built in the constructor before any service call). For nodes
    // whose constructor invokes a service, we'd need DI — but our four side-effecting
    // built-ins all just stash the dep, making this safe.

    public static IEnumerable<object[]> AllSideEffectingNodes()
    {
        // null! suppresses the warning — manifest construction doesn't touch the deps.
        yield return new object[] { new HttpRequestNode(null!) };
        yield return new object[] { new LlmCompleteNode(null!) };
        yield return new object[] { new GitPostPrCommentNode(null!) };
    }

    [Theory]
    [MemberData(nameof(AllSideEffectingNodes))]
    public void Side_effecting_nodes_declare_IsSideEffecting_true(INodeRuntime node)
    {
        node.Manifest.IsSideEffecting.ShouldBeTrue(
            $"node '{node.TypeKey}' has a side-effecting RunAsync (HTTP write / LLM billing / Git API write) " +
            "and MUST declare IsSideEffecting=true. The engine's abandoned-run guard relies on this marker " +
            "to decide whether re-executing the node on retry is safe.");
    }
}
