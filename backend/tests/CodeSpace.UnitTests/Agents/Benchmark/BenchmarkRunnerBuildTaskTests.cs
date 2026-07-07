using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents.Benchmark;

/// <summary>
/// The pure agent-task projection at the heart of slice 2: <see cref="BenchmarkRunner.BuildAgentTask"/> turns a
/// (task, mode, selection) into the <c>AgentTask</c> envelope the executor runs. The SELECTION is what lets the SAME
/// corpus run under the deterministic fake CLI in CI (null selection) and a LIVE agent on demand (real harness +
/// model + gateway credential + autonomy). Pinned directly (InternalsVisibleTo) so the override-vs-fallback contract
/// is proven without a real process / DB.
/// </summary>
[Trait("Category", "Unit")]
public class BenchmarkRunnerBuildTaskTests
{
    private const string Workspace = "/tmp/cs-bench-ws";

    private static BenchmarkTask Task(string harness = "codex-cli") => new()
    {
        Id = "task-a",
        Description = "task-a",
        FixtureRef = "fixture-task-a",
        Goal = "make the check pass",
        Grading = BenchmarkGradingKind.TestsPass,
        TestCommand = new[] { "sh", "check.sh" },
        Harness = harness,
        TimeoutSeconds = 123,
        Modes = new[] { BenchmarkMode.HarnessCli },
    };

    [Fact]
    public void A_null_selection_reproduces_the_pre_selection_default_the_tasks_own_harness_no_model_no_credential_standard_autonomy()
    {
        var agentTask = BenchmarkRunner.BuildAgentTask(Task(harness: "codex-cli"), BenchmarkMode.HarnessCli, Workspace, selection: null);

        agentTask.Harness.ShouldBe("codex-cli", "no override ⇒ the task's own harness");
        agentTask.Model.ShouldBeNull("no selection ⇒ no model (the fake CLI's default)");
        agentTask.ModelCredentialId.ShouldBeNull("no selection ⇒ no gateway credential");
        agentTask.Autonomy.ShouldBe(AgentAutonomyLevel.Standard, "the corpus default — workspace-write, no network");

        // Permissions are DERIVED from autonomy (the executor + harness read Permissions.Network verbatim, not the tier).
        // Derive(Standard) equals the default AgentPermissions, so the null path is byte-identical to the pre-slice envelope.
        agentTask.Permissions.Network.ShouldBe(AgentNetworkAccess.Off, "Standard ⇒ no network — identical to the pre-slice default");
        agentTask.Permissions.WriteScope.ShouldBe(AgentWriteScope.Workspace);

        // Unchanged plumbing: the pre-staged workspace is the sandbox cwd (no RepositoryId), the task's timeout carries.
        agentTask.WorkspaceDirectory.ShouldBe(Workspace);
        agentTask.RepositoryId.ShouldBeNull();
        agentTask.Goal.ShouldBe("make the check pass");
        agentTask.TimeoutSeconds.ShouldBe(123);
    }

    [Fact]
    public void A_real_selection_overrides_harness_model_credential_and_autonomy()
    {
        var credId = Guid.NewGuid();
        var selection = new BenchmarkAgentSelection { Harness = "claude-code", Model = "gw-model", ModelCredentialId = credId, Autonomy = AgentAutonomyLevel.Trusted };

        var agentTask = BenchmarkRunner.BuildAgentTask(Task(harness: "codex-cli"), BenchmarkMode.HarnessCli, Workspace, selection);

        agentTask.Harness.ShouldBe("claude-code", "the selection's harness wins over the task's");
        agentTask.Model.ShouldBe("gw-model");
        agentTask.ModelCredentialId.ShouldBe(credId, "the seeded gateway credential the executor resolves + projects");
        agentTask.Autonomy.ShouldBe(AgentAutonomyLevel.Trusted, "a real coding agent that must reach the gateway + edit to solve");

        // The load-bearing assertion: Trusted must DERIVE Network=On, else a confined live agent can never reach the
        // gateway and the gate would self-skip green forever (the BLOCKER caught in review). The executor reads this, not the tier.
        agentTask.Permissions.Network.ShouldBe(AgentNetworkAccess.On, "Trusted ⇒ network ON — the live agent reaches the gateway when confined");
    }

    [Fact]
    public void A_selection_with_null_fields_falls_back_per_field_not_all_or_nothing()
    {
        // Only the model is set; harness + autonomy fall back to the task's harness + Standard. The override is per-field.
        var selection = new BenchmarkAgentSelection { Model = "gw-model" };

        var agentTask = BenchmarkRunner.BuildAgentTask(Task(harness: "codex-cli"), BenchmarkMode.HarnessCli, Workspace, selection);

        agentTask.Harness.ShouldBe("codex-cli", "null harness ⇒ the task's own");
        agentTask.Model.ShouldBe("gw-model");
        agentTask.ModelCredentialId.ShouldBeNull("null credential ⇒ unset");
        agentTask.Autonomy.ShouldBe(AgentAutonomyLevel.Standard, "null autonomy ⇒ the Standard default");
    }

    [Theory]
    [InlineData(BenchmarkMode.HarnessCliWithMcp, true)]   // the mcp mode opts the run-scoped MCP endpoint in
    [InlineData(BenchmarkMode.HarnessCli, null)]          // the bare cli mode leaves it unset (executor default)
    public void The_mode_sets_the_per_run_mcp_opt_in_independently_of_the_selection(BenchmarkMode mode, bool? expected)
    {
        var agentTask = BenchmarkRunner.BuildAgentTask(Task(), mode, Workspace, selection: null);

        agentTask.EnableMcpEndpoint.ShouldBe(expected, "the mcp opt-in is driven by the mode, not the agent selection");
    }

    // ── Corpus A/B: the output-critic config is the run-level variable a critic-on/critic-off benchmark toggles ──

    [Fact]
    public void The_critic_on_arm_threads_the_output_review_config_onto_the_task()
    {
        var reviewerId = Guid.NewGuid();
        var selection = new BenchmarkAgentSelection { OutputReviewMode = ReviewMode.Improve, ReviewerModelId = reviewerId, MaxReviseRounds = 2 };

        var agentTask = BenchmarkRunner.BuildAgentTask(Task(), BenchmarkMode.HarnessCli, Workspace, selection);

        agentTask.OutputReviewMode.ShouldBe(ReviewMode.Improve, "the critic-ON arm runs the adversarial output critic — the A/B variable under test");
        agentTask.ReviewerModelId.ShouldBe(reviewerId, "the independent reviewer's model row carries onto the task");
        agentTask.MaxReviseRounds.ShouldBe(2, "the critic's revise budget carries onto the task");
    }

    [Fact]
    public void The_critic_off_arm_leaves_the_output_critic_unset_byte_identical()
    {
        // Arm B (and the whole pre-A/B corpus): no critic. A null selection OR a live selection that sets only
        // non-review fields both leave the output critic OFF — the deterministic CI lane is unchanged either way.
        var live = new BenchmarkAgentSelection { Harness = "claude-code", Model = "gw-model", Autonomy = AgentAutonomyLevel.Trusted };

        foreach (var sel in new BenchmarkAgentSelection?[] { null, live })
        {
            var agentTask = BenchmarkRunner.BuildAgentTask(Task(), BenchmarkMode.HarnessCli, Workspace, sel);

            agentTask.OutputReviewMode.ShouldBe(ReviewMode.None, "no OutputReviewMode set ⇒ no critic (byte-identical to the pre-A/B corpus)");
            agentTask.ReviewerModelId.ShouldBeNull("no reviewer model when the critic is off");
            agentTask.MaxReviseRounds.ShouldBeNull("no revise budget when the critic is off");

            // LITERAL byte-identity: the three A/B fields carry [JsonIgnore(WhenWritingDefault/Null)], so a critic-off
            // task serializes WITHOUT them — the persisted task_json is unchanged vs the pre-A/B corpus (the deterministic
            // CI lane can't drift). Serialize with the SAME options the run persistence uses.
            var json = System.Text.Json.JsonSerializer.Serialize(agentTask, CodeSpace.Core.Services.Agents.AgentJson.Options);

            json.ShouldNotContain("outputReviewMode", Case.Insensitive, "critic-off ⇒ the field is omitted from persisted task_json");
            json.ShouldNotContain("reviewerModelId", Case.Insensitive, "critic-off ⇒ the field is omitted from persisted task_json");
            json.ShouldNotContain("maxReviseRounds", Case.Insensitive, "critic-off ⇒ the field is omitted from persisted task_json");
        }
    }
}
