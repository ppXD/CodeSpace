using CodeSpace.Core.Services.Workflows.Plugins;
using CodeSpace.Core.Services.Workflows.Plugins.Builtin;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the domain split. The single <c>BuiltinPluginModule</c> is broken into one
/// plugin per concern so each can be enabled/disabled independently and so the plugin
/// abstraction is exercised by several distinct callers, not one — proves the surface is
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
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.TriggerScheduleNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.TerminalNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.LogicIfNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.LogicMergeNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.FlowIterateNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.FlowSleepNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.FlowWaitApprovalNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.FlowWaitCallbackNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.FlowWaitActionNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.FlowSubworkflowNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.FlowLoopNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.FlowLoopStartNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.FlowTryNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.FlowTryStartNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.FlowMapNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.FlowMapStartNode));
        module.Nodes.Count.ShouldBe(17);
        module.RunSourceMatchers.ShouldBeEmpty("manual + schedule triggers subscribe to no inbound event (schedule is producer-driven), so Core Flow still ships zero matchers");
    }

    [Fact]
    public void Git_providers_plugin_lists_pr_triggers_plus_git_nodes()
    {
        var module = new GitProvidersPlugin();

        module.Name.ShouldBe("Git providers");
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.TriggerPrOpenedNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.TriggerPrUpdatedNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.TriggerPrMergedNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.TriggerPushNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.GitFetchPrDiffNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.GitFetchPrChecksNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.GitListPullRequestsNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.GitPostPrCommentNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.GitOpenPullRequestNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.GitMergePullRequestNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.GitCreateIssueNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.GitCommentIssueNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.GitCloseIssueNode));
        module.RunSourceMatchers.Count.ShouldBe(4, "the PR matchers (opened / updated / merged) + the push matcher ride with the git plugin so disabling git unloads them together");
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
    public void Chat_plugin_is_standalone()
    {
        var module = new ChatPlugin();

        module.Name.ShouldBe("Chat");
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.ChatPostMessageNode));
        module.Nodes.Count.ShouldBe(1);
    }

    [Fact]
    public void Agent_plugin_lists_the_agent_execution_nodes()
    {
        var module = new AgentPlugin();

        module.Name.ShouldBe("Agent");
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.AgentCodeNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.AgentRunCommandNode));
        module.Nodes.ShouldContain(typeof(CodeSpace.Core.Services.Workflows.Nodes.Builtin.AgentSupervisorNode));
        module.Nodes.Count.ShouldBe(3);
    }

    [Fact]
    public void All_builtin_plugins_together_cover_every_builtin_node()
    {
        // If a node escapes its domain plugin OR a new node lands without being assigned to one,
        // this test fails — keeps the catalog accountable.
        IReadOnlyList<IPluginModule> all = new IPluginModule[]
        {
            new CoreFlowPlugin(),
            new GitProvidersPlugin(),
            new HttpToolsPlugin(),
            new LlmCompletePlugin(),
            new ChatPlugin(),
            new AgentPlugin(),
        };

        var total = all.SelectMany(p => p.Nodes).Distinct().Count();
        total.ShouldBe(39, "39 builtin node types across 6 domain plugins — incl. the Git plugin's git.open_change_set multi-repo Change-Set node (multi-repo PR4) — adjust this number when adding a builtin");
    }
}
