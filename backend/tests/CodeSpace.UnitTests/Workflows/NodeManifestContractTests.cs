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
        yield return new object[] { new TriggerPrMergedNode() };
        yield return new object[] { new TriggerPushNode() };
        yield return new object[] { new TriggerScheduleNode() };
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
        new TriggerPrMergedNode().Manifest.Kind.ShouldBe(NodeKind.Trigger);
        new TriggerPushNode().Manifest.Kind.ShouldBe(NodeKind.Trigger);
        new TriggerScheduleNode().Manifest.Kind.ShouldBe(NodeKind.Trigger);
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
        new TriggerPrMergedNode().Manifest.IsManual.ShouldBeFalse(
            "event triggers subscribe to webhooks and MUST keep IsManual=false so deriveActivations still emits their activation");
        new TriggerPushNode().Manifest.IsManual.ShouldBeFalse(
            "event triggers subscribe to webhooks and MUST keep IsManual=false so deriveActivations still emits their activation");
        new TriggerScheduleNode().Manifest.IsManual.ShouldBeFalse(
            "the schedule trigger MUST keep IsManual=false so deriveActivations emits the activation row the producer queries");
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

    // ─── L2 uniformity: x-selector, x-enumLabels, x-long, minimum, title ─────────────────────────────
    //
    // These tests pin the EXACT schema affordances added in the L2 uniformity sweep so that a
    // manifest refactor that silently drops an x-selector (the repo picker) or an x-enumLabels map
    // (friendly option text) produces a loud test failure rather than a confusing regression in the
    // editor UI.  All assertions are on the manifest's schema JSON, not on runtime behaviour — these
    // are purely authoring-UX contracts.

    private static JsonElement GetInputProp(INodeRuntime node, string propName)
    {
        node.Manifest.InputSchema.GetProperty("properties").TryGetProperty(propName, out var prop).ShouldBeTrue(
            $"node '{node.TypeKey}' InputSchema should declare a '{propName}' property");
        return prop;
    }

    private static JsonElement GetConfigProp(INodeRuntime node, string propName)
    {
        node.Manifest.ConfigSchema.GetProperty("properties").TryGetProperty(propName, out var prop).ShouldBeTrue(
            $"node '{node.TypeKey}' ConfigSchema should declare a '{propName}' property");
        return prop;
    }

    // Repository selector — every Git node carrying a repositoryId must present the same x-selector so
    // the editor shows the Pick ⇄ Expression repo picker. Before this sweep only git.pr_review had it;
    // git.fetch_pr_diff and git.post_pr_comment required pasting a raw UUID.
    [Fact]
    public void Git_nodes_all_declare_x_selector_repository_on_repositoryId()
    {
        var nodes = new INodeRuntime[] { new GitFetchPrDiffNode(null!), new GitFetchPrChecksNode(null!), new GitListPullRequestsNode(null!), new GitOpenPullRequestNode(null!), new GitMergePullRequestNode(null!), new GitCreateIssueNode(null!), new GitCommentIssueNode(null!), new GitPostPrCommentNode(null!), new GitPrReviewNode(null!) };
        foreach (var node in nodes)
        {
            var prop = GetInputProp(node, "repositoryId");
            prop.TryGetProperty("x-selector", out var sel).ShouldBeTrue($"'{node.TypeKey}' repositoryId must declare x-selector");
            sel.GetString().ShouldBe("repository", $"'{node.TypeKey}' repositoryId x-selector must be 'repository'");
        }
    }

    // Number description — present on all four Git nodes for discoverability.
    [Fact]
    public void Git_nodes_all_declare_description_on_number()
    {
        var nodes = new INodeRuntime[] { new GitFetchPrDiffNode(null!), new GitFetchPrChecksNode(null!), new GitPostPrCommentNode(null!), new GitPrReviewNode(null!), new GitMergePullRequestNode(null!), new GitCommentIssueNode(null!) };
        foreach (var node in nodes)
        {
            var prop = GetInputProp(node, "number");
            prop.TryGetProperty("description", out _).ShouldBeTrue($"'{node.TypeKey}' number must have a description");
        }
    }

    // x-long on git.post_pr_comment body — markdown is multi-line; without x-long SchemaForm renders it
    // as a single-line input even though the minLength heuristic (>100 chars) never fires for a blank field.
    [Fact]
    public void GitPostPrComment_body_declares_x_long()
    {
        var prop = GetInputProp(new GitPostPrCommentNode(null!), "body");
        prop.TryGetProperty("x-long", out var xlong).ShouldBeTrue("body must declare x-long so SchemaForm renders a textarea");
        xlong.GetBoolean().ShouldBeTrue();
    }

    // x-enumLabels: git.pr_review.verdict — shows "Approve / Request changes / Comment" instead of raw identifiers.
    [Fact]
    public void GitPrReview_verdict_declares_x_enumLabels()
    {
        var prop = GetInputProp(new GitPrReviewNode(null!), "verdict");
        prop.TryGetProperty("x-enumLabels", out var labels).ShouldBeTrue("verdict must declare x-enumLabels for friendly display");
        labels.GetProperty("approve").GetString().ShouldBe("Approve");
        labels.GetProperty("request_changes").GetString().ShouldBe("Request changes");
        labels.GetProperty("comment").GetString().ShouldBe("Comment");
    }

    // x-enumLabels: logic.merge.strategy — "first-non-empty" / "all" are jargon; readable labels are pinned.
    [Fact]
    public void LogicMerge_strategy_declares_x_enumLabels()
    {
        var prop = GetConfigProp(new LogicMergeNode(), "strategy");
        prop.TryGetProperty("x-enumLabels", out var labels).ShouldBeTrue("strategy must declare x-enumLabels for friendly display");
        labels.GetProperty("first-non-empty").GetString().ShouldBe("First branch that ran");
        labels.GetProperty("all").GetString().ShouldBe("Wait for all (barrier)");
    }

    // x-enumLabels: flow.loop termination.logic and termination.conditions[].op.
    [Fact]
    public void FlowLoop_termination_logic_and_op_declare_x_enumLabels()
    {
        var termination = GetConfigProp(new FlowLoopNode(), "termination");
        termination.GetProperty("properties").TryGetProperty("logic", out var logic).ShouldBeTrue();
        logic.TryGetProperty("x-enumLabels", out var logicLabels).ShouldBeTrue("termination.logic must declare x-enumLabels");
        logicLabels.GetProperty("and").GetString().ShouldBe("All conditions match");
        logicLabels.GetProperty("or").GetString().ShouldBe("Any condition matches");

        var condItemProps = termination.GetProperty("properties").GetProperty("conditions")
            .GetProperty("items").GetProperty("properties");
        condItemProps.TryGetProperty("op", out var op).ShouldBeTrue();
        op.TryGetProperty("x-enumLabels", out var opLabels).ShouldBeTrue("conditions[].op must declare x-enumLabels");
        opLabels.GetProperty("eq").GetString().ShouldBe("=");
        opLabels.GetProperty("neq").GetString().ShouldBe("≠");
        opLabels.GetProperty("contains").GetString().ShouldBe("Contains");
        opLabels.GetProperty("not_contains").GetString().ShouldBe("Does not contain");
        opLabels.GetProperty("startsWith").GetString().ShouldBe("Starts with");
        opLabels.GetProperty("endsWith").GetString().ShouldBe("Ends with");
        opLabels.GetProperty("is_empty").GetString().ShouldBe("Is empty");
        opLabels.GetProperty("is_not_empty").GetString().ShouldBe("Is not empty");
    }

    // minimum on flow.sleep.seconds — prevents a footgun where an author sets seconds=0 and the run
    // fails at runtime instead of at save time.
    [Fact]
    public void FlowSleep_seconds_declares_minimum_1()
    {
        var prop = GetConfigProp(new FlowSleepNode(), "seconds");
        prop.TryGetProperty("minimum", out var min).ShouldBeTrue("seconds must declare minimum:1 to catch zero/negative at save time");
        min.GetInt32().ShouldBe(1);
    }

    // x-long on flow.iterate.template — a multi-expression template spanning multiple lines renders
    // poorly in a single-line input; x-long gives the author a textarea.
    [Fact]
    public void FlowIterate_template_declares_x_long()
    {
        var prop = GetConfigProp(new FlowIterateNode(), "template");
        prop.TryGetProperty("x-long", out var xlong).ShouldBeTrue("template must declare x-long so SchemaForm renders a textarea");
        xlong.GetBoolean().ShouldBeTrue();
    }

    // title on flow.iterate.itemAs — "Item As" (the humanized camelCase form) is confusing; a plain
    // title "Item variable name" is immediately understandable.
    [Fact]
    public void FlowIterate_itemAs_declares_plain_language_title()
    {
        var node = new FlowIterateNode();
        node.Manifest.InputSchema.GetProperty("properties").TryGetProperty("itemAs", out var prop).ShouldBeTrue();
        prop.TryGetProperty("title", out var title).ShouldBeTrue("itemAs must declare a title override so the label isn't the confusing 'Item As'");
        title.GetString().ShouldBe("Item variable name");
    }

    // Category alignment: builtin.terminal must use "Logic" to sit alongside flow.loop, flow.try etc.
    // in the editor palette rather than appearing as a lone "Flow" section.
    [Fact]
    public void Terminal_node_category_is_Logic()
    {
        new TerminalNode().Manifest.Category.ShouldBe("Logic",
            "builtin.terminal must use Category='Logic' to group with the other flow/logic nodes in the palette; 'Flow' leaves it as a lone section");
    }

    // PR-trigger schema dedup: every PR trigger must use the SAME serialised ConfigSchema JSON string.
    // If any node drifts (e.g. someone copy-edits one description and forgets the others), this
    // test fails — preventing a subtle activation-model regression where one trigger has different
    // filter semantics from the rest.
    [Fact]
    public void Pr_trigger_nodes_share_identical_ConfigSchema()
    {
        var opened = new TriggerPrOpenedNode().Manifest.ConfigSchema.GetRawText();
        var updated = new TriggerPrUpdatedNode().Manifest.ConfigSchema.GetRawText();
        var merged = new TriggerPrMergedNode().Manifest.ConfigSchema.GetRawText();
        updated.ShouldBe(opened,
            "trigger.pr.opened and trigger.pr.updated must use the same ConfigSchema (PrTriggerSchemas.RepositoriesConfigSchemaJson); " +
            "drift in repo-filter semantics between the two triggers is a silent activation regression");
        merged.ShouldBe(opened,
            "trigger.pr.merged must use the same ConfigSchema (PrTriggerSchemas.RepositoriesConfigSchemaJson) as the other PR triggers; " +
            "drift in repo-filter semantics is a silent activation regression");
    }
}
