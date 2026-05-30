using CodeSpace.Core.Services.Workflows.Plugins;
using CodeSpace.Core.Services.Workflows.Plugins.Builtin;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the 4-domain split. The single <c>BuiltinPluginModule</c> is broken into one
/// plugin per concern so each can be enabled/disabled independently and so the plugin
/// abstraction is exercised by four distinct callers, not one — proves the surface is
/// generic enough for community plugins to use the same surface.
/// </summary>
[Trait("Category", "Unit")]
public class BuiltinPluginModuleTests
{
    [Fact]
    public void Core_flow_plugin_lists_terminal_branch_and_iteration_primitives()
    {
        var module = new CoreFlowPlugin();

        module.Name.ShouldBe("Core Flow");
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.TriggerManualNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.TerminalNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.LogicIfNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.LogicMergeNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.FlowIterateNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.FlowSleepNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.FlowWaitApprovalNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.FlowWaitCallbackNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.FlowSubworkflowNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.FlowLoopNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.FlowLoopStartNode));
        module.Nodes.Count.ShouldBe(11);
        module.RunSourceMatchers.ShouldBeEmpty("the manual trigger subscribes to no event, so Core Flow still ships zero matchers");
    }

    [Fact]
    public void Git_providers_plugin_lists_pr_triggers_plus_git_nodes()
    {
        var module = new GitProvidersPlugin();

        module.Name.ShouldBe("Git providers");
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.TriggerPrOpenedNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.TriggerPrUpdatedNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.GitFetchPrDiffNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.GitPostPrCommentNode));
        module.RunSourceMatchers.Count.ShouldBe(2, "the PR matchers ride with the git plugin so disabling git unloads them together");
    }

    [Fact]
    public void Http_tools_plugin_is_standalone()
    {
        var module = new HttpToolsPlugin();

        module.Name.ShouldBe("HTTP tools");
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.HttpRequestNode));
        module.Nodes.Count.ShouldBe(1);
    }

    [Fact]
    public void Llm_plugin_is_standalone()
    {
        var module = new LlmCompletePlugin();

        module.Name.ShouldBe("LLM");
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.LlmCompleteNode));
        module.Nodes.Count.ShouldBe(1);
    }

    [Fact]
    public void All_four_builtin_plugins_together_cover_every_builtin_node()
    {
        // Sum of all four = 11 nodes. If a node escapes its domain plugin OR a new node lands
        // without being assigned to one, this test fails — keeps the catalog accountable.
        IReadOnlyList<IPluginModule> all = new IPluginModule[]
        {
            new CoreFlowPlugin(),
            new GitProvidersPlugin(),
            new HttpToolsPlugin(),
            new LlmCompletePlugin(),
        };

        var total = all.SelectMany(p => p.Nodes).Distinct().Count();
        total.ShouldBe(17, "17 builtin node types across 4 domain plugins (Core Flow gained flow.loop + flow.loop_start) — adjust this number when adding a builtin");
    }
}
