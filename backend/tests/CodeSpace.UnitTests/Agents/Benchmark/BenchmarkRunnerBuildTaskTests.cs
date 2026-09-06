using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Supervisor;
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

    // BOTH arms are explicit. The bare-cli arm used to leave this null and rely on the ambient env flag being off; the
    // committed default is now ON, so null would give both arms the fabric and silently flatten the A/B into one arm.
    [Theory]
    [InlineData(BenchmarkMode.HarnessCliWithMcp, true)]   // the mcp mode opts the run-scoped MCP fabric in
    [InlineData(BenchmarkMode.HarnessCli, false)]         // the bare cli mode opts OUT — read-only catalog only
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

    // ── P0-B2: the MCP-fabric rule ──────────────────────────────────────────────

    private static Core.Persistence.Entities.AgentRun RunWith(McpFabricEvidence? evidence) => new()
    {
        Id = Guid.NewGuid(),
        ResultJson = System.Text.Json.JsonSerializer.Serialize(new AgentRunResult
        {
            Status = Messages.Enums.AgentRunStatus.Succeeded, ExitReason = "completed", McpEvidence = evidence,
        }, Core.Services.Agents.AgentJson.Options),
    };

    private static McpFabricEvidence Evidence(bool handshake) => new()
    {
        RequestedCatalogMode = "Full", EndpointBound = true, DeclarationWritten = true, ProxyResolved = true,
        HandshakeObserved = handshake, ObservedToolCalls = handshake ? 3 : 0,
    };

    private static readonly BenchmarkGrade PassingGrade = new() { Passed = true, Detail = "tests-passed" };

    [Fact]
    public void The_mcp_arm_with_no_handshake_grades_environment_even_over_a_passing_oracle()
    {
        // A pass without the fabric measured the cli arm under the cli-mcp label — mislabeled A/B data is worse
        // than a lost cell, so the rule overrides in BOTH directions.
        var graded = BenchmarkRunner.ApplyMcpFabricRule(PassingGrade, BenchmarkMode.HarnessCliWithMcp, RunWith(Evidence(handshake: false)));

        graded.Passed.ShouldBeFalse();
        graded.Class.ShouldBe(GradeFailureClass.Environment, "infra, never a model verdict — the corpus counts it InfraUnknown");
        graded.Detail.ShouldStartWith("mcp-required-no-handshake");
    }

    [Fact]
    public void A_handshook_mcp_arm_and_the_cli_arm_pass_through_untouched()
    {
        BenchmarkRunner.ApplyMcpFabricRule(PassingGrade, BenchmarkMode.HarnessCliWithMcp, RunWith(Evidence(handshake: true)))
            .ShouldBeSameAs(PassingGrade);
        BenchmarkRunner.ApplyMcpFabricRule(PassingGrade, BenchmarkMode.HarnessCli, RunWith(Evidence(handshake: false)))
            .ShouldBeSameAs(PassingGrade, "the cli arm never required the fabric");
    }

    [Fact]
    public void A_pre_slice_result_with_no_evidence_is_untouched()
    {
        // Absence of observation is never an observation — an old result must not retro-grade as infra.
        BenchmarkRunner.ApplyMcpFabricRule(PassingGrade, BenchmarkMode.HarnessCliWithMcp, RunWith(evidence: null))
            .ShouldBeSameAs(PassingGrade);
        BenchmarkRunner.ApplyMcpFabricRule(PassingGrade, BenchmarkMode.HarnessCliWithMcp, new Core.Persistence.Entities.AgentRun { Id = Guid.NewGuid(), ResultJson = null })
            .ShouldBeSameAs(PassingGrade);
    }

    // ── The cell's ONE respawn verdict: a mangled wire is infra the benchmark lane may repair, exactly once ──

    /// <summary>The verbatim text the gateway's Anthropic-compat layer kills the claude CLI with — the thing the whole repair exists for, so the decision must turn on nothing less.</summary>
    private const string LiveGatewayFormatFault = "API Error: Content block is not a thinking block";

    [Theory]
    [InlineData(LiveGatewayFormatFault, false, true)]    // the fault, on a cell that has not spent the repair → respawn
    [InlineData(LiveGatewayFormatFault, true, false)]    // the SAME fault on an already-mitigated attempt → the repair does not hold here; the cell stays infra-dead
    [InlineData("tests-failed-exit-1", false, false)]    // a capability verdict — the benchmark is @1, never a general retry
    [InlineData("Anthropic API error (HTTP 429, RateLimited)", false, false)]   // transient, but not the fault this repair fixes
    [InlineData("", false, false)]
    [InlineData(null, false, false)]                     // a clean run reports no error at all
    public void Only_an_unrepaired_gateway_format_fault_buys_a_respawn(string? error, bool alreadyMitigated, bool expectRespawn)
    {
        var dispatched = alreadyMitigated ? AgentRetryCauses.ApplyFormatFaultMitigation(AgentTask_()) : AgentTask_();

        (BenchmarkRunner.RespawnFor(dispatched, error) is not null).ShouldBe(expectRespawn);
    }

    [Fact]
    public void The_respawn_is_the_same_task_repaired_through_the_shared_mitigation()
    {
        // A cell's respawn must be the SAME task on the SAME model — the gateway mangled the wire, not the model — and
        // the repair must be the ONE the helper owns, never a third copy of "fresh conversation + thinking disabled".
        var dispatched = AgentTask_() with { ResumeFromSessionId = "sess-poisoned", RestoredTranscript = "{\"role\":\"user\"}" };

        var respawn = BenchmarkRunner.RespawnFor(dispatched, LiveGatewayFormatFault).ShouldNotBeNull();

        AgentRetryCauses.IsFormatFaultMitigated(respawn).ShouldBeTrue("the benchmark lane must apply the SHARED mitigation, not its own copy of it");
        respawn.ResumeFromSessionId.ShouldBeNull("resuming re-sends the transcript the mangled block lives in — the respawn MUST start fresh");
        respawn.RestoredTranscript.ShouldBeNull();
        respawn.Goal.ShouldBe(dispatched.Goal, "the same task");
        respawn.Model.ShouldBe(dispatched.Model, "the same model — the gateway broke the wire, not the brain");
        respawn.EnableMcpEndpoint.ShouldBe(dispatched.EnableMcpEndpoint, "the cell's arm is untouched — a respawn that quietly changed arms would mislabel the A/B");

        // …and the repair is bought exactly once: the very envelope it produced buys nothing further.
        BenchmarkRunner.RespawnFor(respawn, LiveGatewayFormatFault).ShouldBeNull("a second identical respawn would only re-bill a broken gateway");
    }

    private static AgentTask AgentTask_() => BenchmarkRunner.BuildAgentTask(Task(), BenchmarkMode.HarnessCliWithMcp, Workspace, new BenchmarkAgentSelection { Model = "gw-model" });

    // ── The respawned cell's row: the GRADED attempt's verdict, but BOTH attempts' cost ──

    private static Core.Persistence.Entities.AgentRun Attempt(int inputTokens, int outputTokens, double seconds, AgentRunStatus status = AgentRunStatus.Succeeded, string exitReason = "completed")
    {
        var startedAt = DateTimeOffset.UnixEpoch;

        return new Core.Persistence.Entities.AgentRun
        {
            Id = Guid.NewGuid(),
            Status = status,
            StartedAt = startedAt,
            CompletedAt = startedAt.AddSeconds(seconds),
            ResultJson = System.Text.Json.JsonSerializer.Serialize(new AgentRunResult
            {
                Status = status, ExitReason = exitReason, ReviseRounds = 2,
                TokenUsage = new AgentTokenUsage { InputTokens = inputTokens, OutputTokens = outputTokens },
            }, Core.Services.Agents.AgentJson.Options),
        };
    }

    [Fact]
    public void A_respawned_cell_bills_BOTH_attempts_while_the_verdict_stays_the_graded_ones()
    {
        // The gateway made this cell cost twice. Reading cost off the survivor alone would under-report exactly the
        // cells the respawn count exists to flag — a corpus fighting a broken gateway would look CHEAPER than a clean
        // one. The VERDICT fields still come from the graded (last) attempt, because the oracle judged its tree.
        var died = Attempt(inputTokens: 100, outputTokens: 40, seconds: 3, status: AgentRunStatus.Failed, exitReason: "error");
        var graded = Attempt(inputTokens: 700, outputTokens: 260, seconds: 12);

        var result = BenchmarkRunner.BuildResult(Task(), BenchmarkMode.HarnessCli, new[] { died, graded }, PassingGrade, mcpFullCatalog: false);

        result.TokenUsage.ShouldNotBeNull().InputTokens.ShouldBe(800, "the dead attempt billed 100 input tokens too — the cell cost both");
        result.TokenUsage.OutputTokens.ShouldBe(300);
        result.DurationSeconds.ShouldBe(15, "the cell occupied the instrument for both attempts, not just the survivor's 12s");

        result.FormatFaultRespawns.ShouldBe(1, "two attempts ⇒ exactly one repair was bought");
        result.AgentRunId.ShouldBe(graded.Id, "the row traces to the attempt the grade judged");
        result.RunStatus.ShouldBe(AgentRunStatus.Succeeded, "the graded attempt's terminal status, never the death that preceded it");
        result.ExitReason.ShouldBe("completed", "the graded attempt's exit reason — the dead one's would mislabel the cell");
    }

    [Fact]
    public void An_unrespawned_cell_reports_its_single_attempt_unchanged()
    {
        var only = Attempt(inputTokens: 700, outputTokens: 260, seconds: 12);

        var result = BenchmarkRunner.BuildResult(Task(), BenchmarkMode.HarnessCli, new[] { only }, PassingGrade, mcpFullCatalog: false);

        result.FormatFaultRespawns.ShouldBe(0);
        result.TokenUsage.ShouldNotBeNull().InputTokens.ShouldBe(700, "one attempt ⇒ byte-identical to the pre-respawn projection");
        result.DurationSeconds.ShouldBe(12);
        result.ReviseRounds.ShouldBe(2);
    }

    [Fact]
    public void An_attempt_that_reported_no_usage_never_zeroes_the_one_that_did()
    {
        // The deterministic fake CLI reports no usage at all; a naive sum that treated null as zero — or that let the
        // last attempt win — would erase a real attempt's bill. Same fold the executor sums its revise rounds with.
        var silent = new Core.Persistence.Entities.AgentRun { Id = Guid.NewGuid(), Status = AgentRunStatus.Failed };
        var billed = Attempt(inputTokens: 500, outputTokens: 20, seconds: 8);

        BenchmarkRunner.BuildResult(Task(), BenchmarkMode.HarnessCli, new[] { silent, billed }, PassingGrade, mcpFullCatalog: false)
            .TokenUsage.ShouldNotBeNull().InputTokens.ShouldBe(500);

        BenchmarkRunner.BuildResult(Task(), BenchmarkMode.HarnessCli, new[] { billed, silent }, PassingGrade, mcpFullCatalog: false)
            .TokenUsage.ShouldNotBeNull().InputTokens.ShouldBe(500, "the graded attempt reporting nothing must not erase what the first one billed");
    }

    [Fact]
    public void A_cell_where_no_attempt_recorded_timestamps_reports_no_duration()
    {
        var untimed = new Core.Persistence.Entities.AgentRun { Id = Guid.NewGuid(), Status = AgentRunStatus.Failed };

        BenchmarkRunner.BuildResult(Task(), BenchmarkMode.HarnessCli, new[] { untimed, untimed }, PassingGrade, mcpFullCatalog: false)
            .DurationSeconds.ShouldBeNull("null, never 0 — 0 would enter the latency percentiles as a real measurement");
    }
}
