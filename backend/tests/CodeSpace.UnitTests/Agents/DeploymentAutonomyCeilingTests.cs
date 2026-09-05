using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Tasks.Bounds;
using CodeSpace.Core.Services.Tasks.Bounds.Presets.Deep;
using CodeSpace.Core.Services.Tasks.Bounds.Presets.Quick;
using CodeSpace.Core.Services.Tasks.Bounds.Presets.Standard;
using CodeSpace.Core.Services.Tasks.Capabilities;
using CodeSpace.Core.Services.Tasks.Effort;
using CodeSpace.Core.Services.Tasks.Effort.Classifiers.Heuristic;
using CodeSpace.Core.Services.Tasks.Recipes;
using CodeSpace.Core.Services.Tasks.Recipes.MapFanout;
using CodeSpace.Core.Services.Tasks.Recipes.SingleAgent;
using CodeSpace.Core.Services.Tasks.Recipes.Supervisor;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Core.Settings;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The DEPLOYMENT autonomy ceiling (<c>Sandbox:MaxAutonomy</c>) — the operator's one committed bound over every path
/// that reaches a sandbox. It exists because the launch route's per-effort ceiling is not the only way a run is
/// staged: any team Member may pick a network tier per launch, an API client may post <c>autonomy</c> straight at
/// the launch command, and an authored or REPLAYED <c>agent.run</c> node carries its own tier and its own raw
/// <c>network</c> override with no route to clamp it at all.
///
/// <para>Pinned here as one story because that is what makes it a ceiling rather than three coincidences: the
/// settings KEY (an operator's committed value must survive a refactor), the ROUTER tighten (the launch lane), the
/// NODE clamp (the authored / replay lane), and the fact that all of it is INERT at the committed default.</para>
/// </summary>
[Trait("Category", "Unit")]
public class DeploymentAutonomyCeilingTests
{
    // ── The setting itself ────────────────────────────────────────────────────

    [Fact]
    public void MaxAutonomy_settings_key_is_pinned()
    {
        // Renaming this constant silently restores the TOP tier on every deployment that had lowered its ceiling in
        // a ConfigMap under the old name — a security posture quietly reverting on upgrade, with nothing to notice.
        // Hard-pin the literal so a rename is a compile-time-visible decision (Rule 8).
        RuntimeSettings.MaxAutonomyKey.ShouldBe("Sandbox:MaxAutonomy");
    }

    [Fact]
    public void The_committed_default_is_the_top_tier_so_the_ceiling_is_inert_until_an_operator_lowers_it()
    {
        // Deliberately NOT Trusted, which looks safer and is not: Trusted and Unleashed derive identical sandbox
        // knobs, but AgentToolGate lets only Unleashed run a destructive tool unattended, so a Trusted default would
        // silently re-gate every authored agent.run node pinned to Unleashed. Lowering is an operator DECISION.
        AgentAutonomyPolicy.DefaultDeploymentCeiling.ShouldBe(AgentAutonomyLevel.Unleashed);

        AgentToolGate.Decide(AgentAutonomyLevel.Unleashed, requiresApproval: true).ShouldBe(AgentToolGateDecision.Allow);
        AgentToolGate.Decide(AgentAutonomyLevel.Trusted, requiresApproval: true).ShouldBe(AgentToolGateDecision.RequireApproval,
            customMessage: "this difference is exactly why the default cannot be Trusted — the two tiers are NOT interchangeable");
    }

    [Theory]
    [InlineData("Confined", AgentAutonomyLevel.Confined)]
    [InlineData("standard", AgentAutonomyLevel.Standard)]       // case-insensitive, like every other tier read
    [InlineData("Trusted", AgentAutonomyLevel.Trusted)]
    [InlineData(null, AgentAutonomyLevel.Unleashed)]            // unset ⇒ no deployment bound
    [InlineData("", AgentAutonomyLevel.Unleashed)]
    [InlineData("Standrad", AgentAutonomyLevel.Unleashed)]      // a typo falls back to the committed behaviour…
    public void The_configured_value_resolves_through_the_one_tier_parser(string? configured, AgentAutonomyLevel expected)
    {
        // …rather than to the most restrictive row: a misspelling in a ConfigMap must not sever every run's network
        // across a deployment. Same posture as AgentMemoryCeilingMb, whose unusable value falls back to the
        // committed table instead of being read as "no limit".
        WithCeiling(configured, () => AgentAutonomyPolicy.DeploymentCeiling.ShouldBe(expected));
    }

    // ── The launch lane: one more tighten at the router's caps ────────────────

    [Theory]
    // effort tier          request      deployment ceiling  → the tier the launch actually stamps
    [InlineData(TaskEffortModes.Deep, "Trusted", null, "Trusted")]          // the committed default clamps nothing
    [InlineData(TaskEffortModes.Deep, "Trusted", "Trusted", "Trusted")]     // an equal ceiling clamps nothing
    [InlineData(TaskEffortModes.Deep, "Trusted", "Standard", "Standard")]   // the deployment denies the network tier
    [InlineData(TaskEffortModes.Deep, "Trusted", "Confined", "Confined")]
    [InlineData(TaskEffortModes.Standard, "Trusted", "Standard", "Standard")]
    [InlineData(TaskEffortModes.Quick, "Trusted", "Trusted", "Standard")]   // the ROUTE ceiling still binds when it is lower
    [InlineData(TaskEffortModes.Quick, "Standard", "Confined", "Confined")]
    public async Task The_deployment_ceiling_tightens_the_route_and_the_launch_clamp_follows(string effort, string requested, string? deployment, string expected)
    {
        var plan = await WithCeilingAsync(deployment, () => Router().RouteAsync(RouteRequest(effort), CancellationToken.None));

        var effective = AgentAutonomyPolicy.Clamp(
            AgentAutonomyPolicy.Parse(requested, AgentAutonomyLevel.Standard),
            AgentAutonomyPolicy.Parse(plan.Caps.AutonomyCeiling, AgentAutonomyLevel.Standard));

        effective.ToString().ShouldBe(expected,
            customMessage: $"a '{requested}' request on '{effort}' under deployment ceiling '{deployment ?? "(unset)"}' must resolve to '{expected}' — the launch clamp reads ONE already-bounded ceiling");
    }

    [Fact]
    public async Task An_unbounded_route_keeps_its_blank_ceiling_at_the_committed_default()
    {
        // The regression this guards: rewriting a blank ceiling to a concrete "Unleashed" string would hand the one
        // route nobody bounded exactly the network the launch clamp's fail-closed blank→Standard fold denies it.
        var plan = await WithCeilingAsync(null, () => Router(withPresets: false).RouteAsync(RouteRequest(TaskEffortModes.Deep), CancellationToken.None));

        plan.Caps.AutonomyCeiling.ShouldBeEmpty("no preset resolved, so there is no ceiling to state — and a blank one still folds to Standard at the launch");
    }

    [Fact]
    public async Task An_unbounded_route_is_still_bound_by_a_lowered_deployment_ceiling()
    {
        var plan = await WithCeilingAsync("Confined", () => Router(withPresets: false).RouteAsync(RouteRequest(TaskEffortModes.Deep), CancellationToken.None));

        plan.Caps.AutonomyCeiling.ShouldBe("Confined",
            customMessage: "the blank ceiling's fail-closed fold is Standard, which is ABOVE Confined — without this tighten the unbounded route would out-rank the deployment's own bound");
    }

    // ── The authored / replay lane: the node clamps what no route reaches ─────

    [Theory]
    // authored tier   raw network override   deployment ceiling → the tier + network the AgentTask really carries
    [InlineData("Trusted", true, null, "Trusted", AgentNetworkAccess.On)]           // inert at the committed default
    [InlineData("Trusted", true, "Standard", "Standard", AgentNetworkAccess.Off)]   // the bypass this closes
    [InlineData("Trusted", null, "Standard", "Standard", AgentNetworkAccess.Off)]
    [InlineData("Unleashed", true, "Confined", "Confined", AgentNetworkAccess.Off)]
    [InlineData("Standard", true, "Trusted", "Standard", AgentNetworkAccess.On)]    // a ceiling that grants network changes nothing
    public async Task An_authored_agent_run_node_is_clamped_to_the_deployment_ceiling(string authored, bool? network, string? deployment, string expectedTier, AgentNetworkAccess expectedNetwork)
    {
        // A raw "network": true is written straight onto the resolved permissions AFTER the tier baseline, so
        // clamping the TIER alone leaves it granting the internet the host's ceiling denies every tier under it.
        var config = NodeConfig(authored, network);

        var task = await WithCeilingAsync(deployment, () => StageAsync(config));

        task.Autonomy.ShouldBe(AgentAutonomyPolicy.Parse(expectedTier, AgentAutonomyLevel.Standard));
        task.Permissions.Network.ShouldBe(expectedNetwork,
            customMessage: $"an authored '{authored}' node with network={network?.ToString() ?? "(unset)"} under deployment ceiling '{deployment ?? "(unset)"}' reaches the runner with network {expectedNetwork}");
    }

    [Fact]
    public async Task A_raw_readOnly_false_cannot_out_rank_a_read_only_deployment_ceiling()
    {
        // The sibling of the network bypass: readOnly is the same kind of raw per-field override, so it gets the
        // same last word. Confined is the only ceiling whose own tier withholds workspace writes.
        var config = NodeConfig("Trusted", network: null);
        config["readOnly"] = JsonSerializer.SerializeToElement(false);

        var task = await WithCeilingAsync("Confined", () => StageAsync(config));

        task.Permissions.WriteScope.ShouldBe(AgentWriteScope.ReadOnly);
    }

    // ── The spec default the ceiling ultimately lands on ──────────────────────

    [Fact]
    public void A_sandbox_spec_that_says_nothing_about_the_network_is_severed()
    {
        // Fail-closed by construction: while this defaulted to true, a spec builder that simply forgot the field
        // granted full egress with no symptom to notice. Every builder that DOES need a remote says so explicitly.
        new SandboxSpec { Command = "anything" }.AllowNetwork.ShouldBeFalse();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static EffortRouter Router(bool withPresets = true) => new(
        new EffortClassifierRegistry(new IEffortClassifier[] { new HeuristicEffortClassifier() }),
        new TaskRecipeRegistry(new ITaskRecipe[] { new SingleAgentRecipe(), new MapFanoutRecipe(), new SupervisorRecipe() }),
        new BoundsPresetRegistry(withPresets ? new IBoundsPreset[] { new QuickBoundsPreset(), new StandardBoundsPreset(), new DeepBoundsPreset() } : Array.Empty<IBoundsPreset>()),
        new CapabilityProbeRegistry(Array.Empty<ICapabilityProbe>()));

    private static EffortRouteRequest RouteRequest(string effort) => new()
    {
        Seed = new TaskLaunchSeed { Goal = "Install the dependencies and push the branch", SurfaceKind = "test", TeamId = Guid.NewGuid() },
        RequestedEffort = effort,
    };

    private static Dictionary<string, JsonElement> NodeConfig(string autonomy, bool? network)
    {
        var config = new Dictionary<string, JsonElement>
        {
            ["goal"] = JsonSerializer.SerializeToElement("Fix the tests"),
            ["harness"] = JsonSerializer.SerializeToElement("codex-cli"),
            ["autonomyLevel"] = JsonSerializer.SerializeToElement(autonomy),
        };

        if (network is { } value) config["network"] = JsonSerializer.SerializeToElement(value);

        return config;
    }

    /// <summary>Run the node to its agent-run suspension and read back the REAL <see cref="AgentTask"/> envelope the runner would be handed — the permissions the sandbox enforces, never the displayed config.</summary>
    private static async Task<AgentTask> StageAsync(Dictionary<string, JsonElement> config)
    {
        var result = await new AgentCodeNode().RunAsync(NodeContext(config), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Suspended);

        return JsonSerializer.Deserialize<AgentTask>(result.SuspendUntil!.Payload, AgentJson.Options)!;
    }

    private static NodeRunContext NodeContext(Dictionary<string, JsonElement> config) => new()
    {
        Inputs = new Dictionary<string, JsonElement>(),
        Config = config,
        RawInputs = JsonDocument.Parse("{}").RootElement,
        RawConfig = JsonDocument.Parse("{}").RootElement,
        Scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement>() },
        Logger = NullLogger.Instance,
        Observability = NodeObservability.NoOp,
        ResumePayload = null,
    };

    /// <summary>Bind the deployment ceiling for the duration of one assertion, through the REAL configuration read — a null value is an unconfigured deployment, not a bypass of the mapping under test.</summary>
    private static void WithCeiling(string? configured, Action assert)
    {
        using (RuntimeSettings.Override(Read(configured))) assert();
    }

    private static async Task<T> WithCeilingAsync<T>(string? configured, Func<Task<T>> act)
    {
        using (RuntimeSettings.Override(Read(configured))) return await act();
    }

    private static RuntimeSettings Read(string? configured) =>
        RuntimeSettings.Read(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [RuntimeSettings.MaxAutonomyKey] = configured })
            .Build());
}
