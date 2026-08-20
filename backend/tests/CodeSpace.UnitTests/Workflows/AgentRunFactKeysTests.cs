using System.Reflection;
using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: the enforceable version of "a third harness lands by dropping a folder" for the three shared run facts —
/// the session id a warm retry resumes from, the token usage the run is billed by, and the model it ran. Before
/// <see cref="IAgentHarnessRunFactKeys"/> these were extracted by a key table the shared readers held, a UNION over
/// the two shipped adapters, so a third harness that spelled any of them otherwise got three nulls with nothing said:
/// every warm retry cold-started, the run reported no cost, and its model column stayed empty.
///
/// <para>Three things are pinned here. (1) A harness whose stream spells all three differently from both shipped
/// adapters gets all three, by declaring and nothing else — this test failed with <c>null|null|null</c> before the
/// declaration seam existed and is the drift detector for it. (2) BOTH shipped adapters extract exactly what the
/// pre-declaration table extracted, over their own real-shaped lines and their own <c>ParseEvents</c> — a differential
/// against <see cref="AgentRunFactKeys.Fallback"/>, which is that table transcribed. (3) A fact missing from a harness
/// that declared nothing is reported as unestablished, which is what the executor logs.</para>
/// </summary>
[Trait("Category", "Unit")]
public class AgentRunFactKeysTests
{
    /// <summary>A third harness's stream: none of these spellings appears in either shipped adapter's stream, and none is in the fallback union.</summary>
    private static readonly string[] ThirdPartyStream =
    {
        """{"event":"session.open","conversation_id":"conv-third-1","engine":"orion-3"}""",
        """{"event":"turn.done","metrics":{"tokens":{"tokens_in":120,"tokens_out":34}}}""",
    };

    /// <summary>Real-shaped <c>claude --output-format stream-json</c> lines: the <c>init</c> line names the session + model, the <c>result</c> line carries the usage, and an assistant turn nests a per-turn usage + model this adapter deliberately does not read.</summary>
    private static readonly string[] ClaudeStream =
    {
        """{"type":"system","subtype":"init","session_id":"11111111-2222-3333-4444-555555555555","model":"claude-opus-4-8","tools":["Bash"]}""",
        """{"type":"assistant","message":{"model":"claude-haiku-4-5","usage":{"input_tokens":9,"output_tokens":1},"content":[{"type":"text","text":"working"}]}}""",
        """{"type":"result","subtype":"success","session_id":"11111111-2222-3333-4444-555555555555","is_error":false,"result":"done","usage":{"input_tokens":1200,"output_tokens":340}}""",
    };

    /// <summary>Real-shaped <c>codex exec --json</c> lines: <c>thread.started</c> names the thread, 0.142.x's <c>turn.completed</c> carries <c>usage</c>, and an older build's cumulative total sits under <c>info.total_token_usage</c>.</summary>
    private static readonly string[] CodexStream =
    {
        """{"type":"thread.started","thread_id":"019f01b0-6aad-72a0-a14e-1c9fc9d1387a"}""",
        """{"type":"item.completed","item":{"type":"agent_message","text":"done"}}""",
        """{"type":"token_count","info":{"total_token_usage":{"input_tokens":50,"output_tokens":7}}}""",
        """{"type":"turn.completed","usage":{"input_tokens":11860,"output_tokens":3}}""",

        // ORDER probe, and the only synthetic line here: no codex build is known to carry both locations on one line.
        // It exists so the pin can fail on a re-ordered container list — the order decides which figure a line
        // carrying two is billed for, and a corpus of one-location-per-line cannot tell two orders apart.
        """{"type":"turn.completed","usage":{"input_tokens":900,"output_tokens":80},"info":{"total_token_usage":{"input_tokens":901,"output_tokens":81}}}""",
    };

    private static AgentEvent Event(string dataJson) => new()
    {
        Kind = AgentEventKind.Warning,
        Text = "",
        Data = JsonDocument.Parse(dataJson).RootElement.Clone(),
    };

    [Fact]
    public void A_third_harness_that_spells_the_three_facts_its_own_way_still_reports_them()
    {
        var harness = new ThirdPartyHarness();

        var facts = AgentRunFacts.From(ThirdPartyStream.SelectMany(harness.ParseEvents).ToList(), harness);

        facts.SessionId.ShouldBe("conv-third-1", "a harness that names its conversation handle its own way must still be warm-resumable");
        facts.Model.ShouldBe("orion-3", "a harness that names its model its own way must still have the run report a model");
        facts.TokenUsage.ShouldNotBeNull("a harness that nests its usage its own way must still have the run report a cost");
        facts.TokenUsage!.InputTokens.ShouldBe(120);
        facts.TokenUsage.OutputTokens.ShouldBe(34);
        facts.UnestablishedFacts.ShouldBeEmpty("a harness that declared its spellings has no unestablished fact");
    }

    /// <summary>
    /// The no-regression pin: for each shipped adapter, reading its own stream with its OWN declaration must produce
    /// exactly what reading it with the pre-declaration union produces. This is what proves the seam moved knowledge
    /// without moving behaviour — a narrowed key list, a re-ordered usage container (which decides the billed figure
    /// on a line carrying two), or a dropped envelope fails here rather than in production.
    /// </summary>
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public void A_shipped_harness_extracts_exactly_what_the_pre_declaration_table_extracted(string kind)
    {
        var harness = kind == "claude" ? new ClaudeCodeHarness() : (IAgentHarness)new CodexHarness();
        var events = (kind == "claude" ? ClaudeStream : CodexStream).SelectMany(harness.ParseEvents).ToList();

        var declared = AgentRunFacts.From(events, harness);
        var legacy = AgentRunFacts.From(events);

        Describe(declared).ShouldBe(Describe(legacy), $"{harness.Kind} must extract the same three facts under its own declaration as under the union the readers used to hold");
        declared.SessionId.ShouldNotBeNull("a vacuous pin proves nothing — this corpus must actually carry a session id");
        declared.TokenUsage.ShouldNotBeNull("a vacuous pin proves nothing — this corpus must actually carry a usage");
    }

    /// <summary>
    /// Every SHIPPED adapter declares, so the fallback union is only ever a third party's compatibility floor and the
    /// executor's unestablished-facts warning can never fire for a harness this repo owns. A new shipped adapter that
    /// forgets to declare fails here rather than shipping a run whose warm retries silently cold-start.
    /// </summary>
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public void Every_shipped_adapter_declares_where_its_stream_spells_the_run_facts(string kind)
    {
        var harness = kind == "claude" ? new ClaudeCodeHarness() : (IAgentHarness)new CodexHarness();

        var declaring = harness.ShouldBeAssignableTo<IAgentHarnessRunFactKeys>($"{harness.Kind} must own where its own stream spells the three run facts, not lean on the fallback union");

        declaring!.RunFactKeys.SessionIdKeys.ShouldNotBeEmpty("a shipped adapter has to be warm-resumable, which needs a session-id spelling");
        declaring.RunFactKeys.InputTokenKeys.ShouldNotBeEmpty("a shipped adapter's runs have to be priceable");
        declaring.RunFactKeys.OutputTokenKeys.ShouldNotBeEmpty("a shipped adapter's runs have to be priceable");
    }

    /// <summary>Claude's assistant turn nests a model + usage under <c>message</c>; neither adapter reads them, and declaring must not start.</summary>
    [Fact]
    public void Claude_reads_the_init_lines_model_and_the_result_lines_usage_not_an_assistant_turns()
    {
        var harness = new ClaudeCodeHarness();

        var facts = AgentRunFacts.From(ClaudeStream.SelectMany(harness.ParseEvents).ToList(), harness);

        facts.Model.ShouldBe("claude-opus-4-8", "the init line's model is the run's, never a per-turn message.model");
        facts.TokenUsage!.InputTokens.ShouldBe(1200, "the result line's usage is the run total, never a per-turn message.usage");
    }

    /// <summary>An adapter that declares nothing keeps the behaviour it had: the fallback union still reads a stream spelled the way it always was.</summary>
    [Fact]
    public void A_harness_that_declares_nothing_still_reads_a_stream_spelled_the_fallback_way()
    {
        var events = new[]
        {
            Event("""{"type":"thread.started","thread_id":"thr-legacy-1"}"""),
            Event("""{"type":"result","model":"some-model","usage":{"prompt_tokens":7,"completion_tokens":2}}"""),
        };

        var facts = AgentRunFacts.From(events, new UndeclaredHarness());

        facts.SessionId.ShouldBe("thr-legacy-1");
        facts.Model.ShouldBe("some-model");
        facts.TokenUsage!.InputTokens.ShouldBe(7);
        facts.UnestablishedFacts.ShouldBeEmpty("nothing is unestablished when the fallback table found all three");
    }

    /// <summary>
    /// The signal that replaces silence. A null fact from a harness that declared nothing is UNESTABLISHED — the
    /// fallback table may simply not know that stream's spelling — and is named so the executor can log it. The same
    /// null from a harness that DID declare is an absence the harness stated (Codex names no model in-stream), and
    /// says nothing.
    /// </summary>
    [Theory]
    [InlineData(false, "session id, token usage, model")]
    [InlineData(true, "")]
    public void A_missing_fact_is_unestablished_only_when_the_harness_declared_nothing(bool declares, string expected)
    {
        IAgentHarness harness = declares ? new ThirdPartyHarness() : new UndeclaredHarness();
        var events = new[] { Event("""{"event":"heartbeat","note":"no fact of any kind here"}""") };

        var facts = AgentRunFacts.From(events, harness);

        string.Join(", ", facts.UnestablishedFacts).ShouldBe(expected);
    }

    /// <summary>
    /// The fallback union's own claim, enforced instead of remembered. <see cref="AgentRunFactKeys.Fallback"/>
    /// documents itself as the spellings the two shipped adapters use, and <c>CodexHarness</c>'s declaration documents
    /// itself as that same table "in the SAME order" — so one ordered list lives as two literals in two files, and one
    /// of them is a BILLING order: the first usage container holding both counts is the figure the run is priced by.
    /// The no-regression differential above only sees a divergence its corpus can distinguish, so a container declared
    /// on one side only, or a swap among the positions no fixture line reaches, passes it (verified: it did). This
    /// compares the lists themselves — every declared key must appear in the fallback, in the same relative order —
    /// across EVERY key list on the record, found by reflection so a seventh list added later is covered without an
    /// edit here.
    /// </summary>
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public void The_fallback_union_holds_every_shipped_adapters_keys_in_the_same_order(string kind)
    {
        var harness = kind == "claude" ? new ClaudeCodeHarness() : (IAgentHarness)new CodexHarness();
        var declared = harness.ShouldBeAssignableTo<IAgentHarnessRunFactKeys>()!.RunFactKeys;

        foreach (var list in KeyListProperties())
        {
            var mine = (IReadOnlyList<string>)list.GetValue(declared)!;
            var union = (IReadOnlyList<string>)list.GetValue(AgentRunFactKeys.Fallback)!;

            IsSubsequence(mine, union).ShouldBeTrue(WhyTheyMustAgree(harness.Kind, list.Name, mine, union));
        }
    }

    /// <summary>Why a failure of the pin above matters, and which direction to fix it in — never by relaxing the pin.</summary>
    private static string WhyTheyMustAgree(string kind, string listName, IReadOnlyList<string> mine, IReadOnlyList<string> union) =>
        $"{kind} declares {listName} = [{string.Join(", ", mine)}], which is not [{string.Join(", ", union)}] (AgentRunFactKeys.Fallback) read in order. "
        + "The fallback documents itself as the union of the shipped adapters' spellings, so a key one of them declares and it lacks makes that doc false and leaves an undeclared third harness a narrower floor than the readers used to give it. "
        + $"For {nameof(AgentRunFactKeys.UsageContainers)} the ORDER is what a run is billed by — the first container holding both counts wins — so a swap here re-prices every line carrying two usage objects. "
        + "Fix by making the two literals agree (add to the fallback, or restore the order), not by loosening this assertion.";

    /// <summary>Every ordered key list on the record — by reflection, so a list added to <see cref="AgentRunFactKeys"/> later is pinned the day it appears rather than the day someone remembers.</summary>
    private static IEnumerable<PropertyInfo> KeyListProperties() =>
        typeof(AgentRunFactKeys).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.PropertyType == typeof(IReadOnlyList<string>));

    /// <summary>True when every element of <paramref name="inner"/> appears in <paramref name="outer"/> in the same relative order (a supersequence test, not a set test — the order is the part that decides billing).</summary>
    private static bool IsSubsequence(IReadOnlyList<string> inner, IReadOnlyList<string> outer)
    {
        var next = 0;

        foreach (var key in inner)
        {
            while (next < outer.Count && outer[next] != key) next++;

            if (next == outer.Count) return false;

            next++;
        }

        return true;
    }

    private static string Describe(AgentRunFacts facts) =>
        $"{facts.SessionId ?? "null"}|{facts.Model ?? "null"}|{facts.TokenUsage?.InputTokens.ToString() ?? "null"}/{facts.TokenUsage?.OutputTokens.ToString() ?? "null"}";

    /// <summary>A harness double whose stream spells all three facts its own way — the third adapter this seam exists for. It retains each line's root as <c>Data</c>, the obligation <see cref="IAgentHarness.ParseEvents"/> states.</summary>
    private sealed class ThirdPartyHarness : IAgentHarness, IAgentHarnessRunFactKeys
    {
        public string Kind => "orion-cli";
        public string Version => "1.0.0";
        public IReadOnlyList<string> Models { get; } = new[] { "orion-3" };

        public AgentRunFactKeys RunFactKeys { get; } = new()
        {
            SessionIdKeys = new[] { "conversation_id" },
            ModelKeys = new[] { "engine" },
            InputTokenKeys = new[] { "tokens_in" },
            OutputTokenKeys = new[] { "tokens_out" },
            UsageContainers = new[] { "metrics.tokens" },
        };

        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "orion" };

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) => new[] { Event(rawLine) };

        public IAgentEventFolder CreateFolder() => new TestEventFolder((_, _) => new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" });
    }

    /// <summary>The same double with no declaration — a harness read under <see cref="AgentRunFactKeys.Fallback"/>.</summary>
    private sealed class UndeclaredHarness : IAgentHarness
    {
        public string Kind => "undeclared-cli";
        public string Version => "1.0.0";
        public IReadOnlyList<string> Models { get; } = new[] { "some-model" };

        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "undeclared" };

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) => new[] { Event(rawLine) };

        public IAgentEventFolder CreateFolder() => new TestEventFolder((_, _) => new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" });
    }
}
