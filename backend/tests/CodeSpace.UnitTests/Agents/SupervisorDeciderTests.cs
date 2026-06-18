using System.Text.Json;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the PR-E E3 real decider (<see cref="LlmSupervisorDecider"/>) + the projector
/// (<see cref="SupervisorDecisionProjector"/>), driven against a DETERMINISTIC fake at the
/// <see cref="IStructuredLLMClient"/> boundary (the honest seam — only the network call is replaced). Pins:
/// the decider folds the turn context into a prompt + projects a schema-valid model decision into a canonical
/// <see cref="SupervisorDecision"/>; each verb projects to its canonical payload; a missing model + an unknown
/// kind both FAIL CLOSED to a terminal stop (no crash).
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorDeciderTests
{
    private static SupervisorTurnContext Context(int turnNumber = 0, params SupervisorPriorDecision[] prior) =>
        new() { Goal = "ship the feature", TurnNumber = turnNumber, PriorDecisions = prior };

    // ── The decider folds context → a schema-valid canonical decision ────────────────

    [Fact]
    public async Task The_decider_projects_a_plan_model_decision_into_a_canonical_plan()
    {
        var model = new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.Plan,
            Plan = new SupervisorPlanPayload
            {
                Goal = "ship",
                Subtasks = new[] { new SupervisorPlannedSubtask { Id = "s1", Title = "Audit", Instruction = "audit it" } },
            },
        };

        var decision = await Decider(model).DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Plan);
        decision.IsTerminal.ShouldBeFalse();

        var subtasks = JsonDocument.Parse(decision.PayloadJson).RootElement.GetProperty("subtasks");
        subtasks.GetArrayLength().ShouldBe(1);
        subtasks[0].GetProperty("id").GetString().ShouldBe("s1");
    }

    [Fact]
    public async Task The_user_prompt_folds_goal_turn_and_prior_decisions()
    {
        var prior = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"subtasks":[]}""", OutcomeJson = """{"planned":[]}""",
        };

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 1, prior));

        prompt.ShouldContain("ship the feature", Case.Insensitive);
        prompt.ShouldContain("Turn: 1");
        prompt.ShouldContain(SupervisorDecisionKinds.Plan, Case.Insensitive, "the prior plan is folded into the prompt so the decider can spawn over it");
    }

    [Fact]
    public void The_user_prompt_surfaces_each_spawned_agents_status_summary_and_error()
    {
        // SOTA #2 — the decider must SEE what its agents produced. A spawn decision whose outcome carries the
        // folded agentResults[] renders each agent's status + summary + error verbatim into the prompt.
        var spawn = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"subtaskIds":["s1","s2"]}""",
            OutcomeJson = """{"agentRunIds":["a","b"],"agentCount":2,"agentResults":[{"agentRunId":"a","status":"Succeeded","summary":"added the endpoint"},{"agentRunId":"b","status":"Failed","error":"tests failed: NRE in FooService"}]}""",
        };

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 2, spawn));

        prompt.ShouldContain("Succeeded");
        prompt.ShouldContain("added the endpoint", Case.Insensitive, "the decider sees a successful agent's summary");
        prompt.ShouldContain("Failed");
        prompt.ShouldContain("NRE in FooService", Case.Insensitive, "the decider sees the failed agent's error — the signal it needs to retry");
    }

    [Fact]
    public void The_user_prompt_is_UNCHANGED_by_per_repo_RepositoryResults()
    {
        // Resolver loop #379 S7-B — surfacing per-repo RepositoryResults into the compact must NOT change what the
        // decider SEES: the labeled rendering reads agentResults field-selectively (status / summary / error), never
        // the raw per-repo array. So a multi-repo spawn's prompt is byte-identical to the same spawn without per-repo
        // data — single-repo decider behaviour is preserved and a multi-repo run adds no prompt bloat. Built via the
        // REAL fold helper (valid Guids) so the labeled path — not the raw-jsonb fallback — is exercised.
        var agentId = Guid.NewGuid();
        var spawnOutcome = $$"""{"agentRunIds":["{{agentId}}"],"agentCount":1}""";

        SupervisorAgentResult Result(IReadOnlyList<RepositoryRunResult> repos) => new()
        {
            AgentRunId = agentId, Status = "Succeeded", Summary = "coordinated change", ProducedBranch = "codespace/agent/api", RepositoryResults = repos,
        };

        var withoutPerRepo = SupervisorOutcome.FoldAgentResults(spawnOutcome, new[] { Result(Array.Empty<RepositoryRunResult>()) });
        var withPerRepo = SupervisorOutcome.FoldAgentResults(spawnOutcome, new[] { Result(new[]
        {
            new RepositoryRunResult { Alias = "repo", RepositoryId = Guid.NewGuid(), ProducedBranch = "codespace/agent/api", BaseBranch = "main", Access = WorkspaceAccess.Write },
            new RepositoryRunResult { Alias = "web", RepositoryId = Guid.NewGuid(), ProducedBranch = "codespace/agent/web", BaseBranch = "develop", Access = WorkspaceAccess.Write },
        }) }) ;

        SupervisorPriorDecision Spawn(string outcome) => new()
        {
            Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"subtaskIds":["s1"]}""", OutcomeJson = outcome,
        };

        var promptWithout = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 2, Spawn(withoutPerRepo)));
        var promptWith = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 2, Spawn(withPerRepo)));

        promptWith.ShouldBe(promptWithout, "per-repo RepositoryResults are not rendered into the decider prompt — the field-selective labeled rendering keeps single-repo behaviour identical and adds no multi-repo bloat");
        promptWith.ShouldNotContain("codespace/agent/web", Case.Insensitive, "the per-repo branches never leak into the prompt as raw text");
    }

    [Fact]
    public void The_system_prompt_instructs_inspect_and_retry_without_an_unconditional_merge_then_stop_rail()
    {
        // The visibility fold is necessary-but-not-sufficient: the rails must INSTRUCT the model to act on what it
        // sees. The old fixed "plan, then spawn, then merge, then stop" rail is REPLACED (not appended) — co-presence
        // would leave two conflicting directives and the rail would win at temp 0.2.
        var system = LlmSupervisorDecider.SystemPromptForTest;

        system.ShouldContain("retry", Case.Insensitive, "the supervisor is instructed to retry a failed subtask");
        system.ShouldContain("inspect", Case.Insensitive, "...after inspecting each agent's status/error");
        system.ShouldNotContain("then merge, then stop", Case.Insensitive, "the unconditional merge-then-stop rail is GONE — it would override the conditional retry guidance");
    }

    // ── Resolver loop #379 S1: the decider PERCEIVES an integration conflict ─────────

    /// <summary>
    /// A merge outcome carrying a conflicted on-disk integration block, shaped EXACTLY as ProjectIntegrationResult emits
    /// it: an APPLIED contribution carries fallbackBranch null (LocalGitBranchIntegrator.Applied() sets none); only the
    /// CONFLICTED contribution carries its preserved branch (FallbackBranch = ProducedBranch). The reader must collect
    /// only the non-applied branch — the applied agent's branch is not surfaced here (the resolver's full re-merge set
    /// is assembled from the spawn's agent results, not this block).
    /// </summary>
    private const string ConflictedMergeOutcome = """
        {"synthesis":{"text":"combined"},"integration":{"status":"Conflicted","integratedBranch":null,"appliedCount":0,"reason":"a contribution conflicted while integrating","excludedAgents":[],"outcomes":[
          {"label":"agent-a","disposition":"Applied","reason":null,"conflictedFiles":[],"fallbackBranch":null},
          {"label":"agent-b","disposition":"Conflicted","reason":"textual conflict","conflictedFiles":["src/Foo.cs","src/Bar.cs"],"fallbackBranch":"codespace/agent/bbb"}
        ]}}
        """;

    private static SupervisorPriorDecision MergeDecision(string outcomeJson) => new()
    {
        Id = Guid.NewGuid(), Sequence = 3, DecisionKind = SupervisorDecisionKinds.Merge, Status = SupervisorDecisionStatus.Succeeded,
        PayloadJson = "{}", OutcomeJson = outcomeJson,
    };

    [Fact]
    public void ReadIntegration_parses_a_conflicted_block_aggregating_files_and_preserved_branches()
    {
        var integration = SupervisorOutcome.ReadIntegration(ConflictedMergeOutcome);

        integration.ShouldNotBeNull();
        integration!.IsConflicted.ShouldBeTrue();
        integration.Status.ShouldBe("Conflicted");
        integration.ConflictedFiles.ShouldBe(new[] { "src/Foo.cs", "src/Bar.cs" });
        integration.PreservedBranches.ShouldBe(new[] { "codespace/agent/bbb" }, "only a NON-applied contribution carries a fallbackBranch (the integrator's contract); the applied agent's branch is not in this block");
        integration.Reason.ShouldBe("a contribution conflicted while integrating");
        integration.IntegratedBranch.ShouldBeNull();
    }

    [Fact]
    public void ReadIntegration_reads_a_clean_integration_as_not_conflicted()
    {
        var clean = SupervisorOutcome.ReadIntegration("""{"integration":{"status":"Clean","integratedBranch":"codespace/integration/run/turn1","appliedCount":2,"reason":null,"outcomes":[]}}""");

        clean.ShouldNotBeNull();
        clean!.IsConflicted.ShouldBeFalse();
        clean.IntegratedBranch.ShouldBe("codespace/integration/run/turn1");
        clean.ConflictedFiles.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"planned":[]}""")]            // a merge/plan outcome with no integration block
    [InlineData("""{"integration":{"reason":"x"}}""")]  // integration object but no status
    [InlineData("""{"integration":"x"}""")]            // integration present but not an object
    [InlineData("""{"integration":{"status":5}}""")]    // status present but not a string
    public void ReadIntegration_returns_null_when_absent_or_malformed(string? outcomeJson)
    {
        SupervisorOutcome.ReadIntegration(outcomeJson).ShouldBeNull();
    }

    [Fact]
    public void The_user_prompt_renders_a_conflicted_merge_as_a_legible_actionable_block()
    {
        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 3, MergeDecision(ConflictedMergeOutcome)));

        prompt.ShouldContain("INTEGRATION CONFLICTED", Case.Insensitive, "the decider sees the conflict framed as actionable, not buried in raw jsonb");
        prompt.ShouldContain("src/Foo.cs", Case.Insensitive);
        prompt.ShouldContain("src/Bar.cs", Case.Insensitive, "the conflicted files are named so a resolver knows what to reconcile");
        prompt.ShouldContain("codespace/agent/bbb", Case.Insensitive, "the preserved branches are named — the resolver's inputs");
        prompt.ShouldContain("spawn ONE agent", Case.Insensitive, "the resolve move is spelled out");
        prompt.ShouldContain("stop to leave the conflict for a human", Case.Insensitive, "the fail-safe move is offered too");
    }

    [Fact]
    public void A_clean_merge_does_NOT_render_the_conflict_block()
    {
        // Behavior-preserving: only a CONFLICTED integration gets the legible block; a clean merge keeps the compact line.
        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(turnNumber: 3, MergeDecision("""{"integration":{"status":"Clean","integratedBranch":"b","outcomes":[]}}""")));

        prompt.ShouldNotContain("INTEGRATION CONFLICTED");
    }

    [Fact]
    public void The_system_prompt_offers_the_resolve_or_stop_choice_and_forbids_an_unverified_resolution()
    {
        var system = LlmSupervisorDecider.SystemPromptForTest;

        system.ShouldContain("INTEGRATION CONFLICTED", Case.Insensitive);
        system.ShouldContain("never accept an unverified resolution", Case.Insensitive, "the safety floor is in the rails — no blind accept");
    }

    [Fact]
    public async Task A_deployment_with_no_structured_provider_fails_closed_to_a_terminal_stop()
    {
        var decider = new LlmSupervisorDecider(new FakeRegistry(structured: null));

        var decision = await decider.DecideAsync(Context(), CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Stop);
        decision.IsTerminal.ShouldBeTrue("no model → a clean one-turn no-op stop, never a crash");
    }

    // ── The projector maps each verb to its canonical payload ────────────────────────

    [Fact]
    public void Spawn_projects_the_subtask_ids()
    {
        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.Spawn,
            Spawn = new SupervisorSpawnPayload { SubtaskIds = new[] { "s1", "s2" } },
        });

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Spawn);
        JsonDocument.Parse(decision.PayloadJson).RootElement.GetProperty("subtaskIds").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public void Stop_is_terminal_and_retry_merge_ask_human_are_not()
    {
        Project(SupervisorDecisionKinds.Stop, m => m with { Stop = new SupervisorStopPayload { Outcome = "done" } }).IsTerminal.ShouldBeTrue();
        Project(SupervisorDecisionKinds.Retry, m => m with { Retry = new SupervisorRetryPayload { SubtaskId = "s1" } }).IsTerminal.ShouldBeFalse();
        Project(SupervisorDecisionKinds.Merge, m => m with { Merge = new SupervisorMergePayload() }).IsTerminal.ShouldBeFalse();
        Project(SupervisorDecisionKinds.AskHuman, m => m with { AskHuman = new SupervisorAskHumanPayload { Question = "?" } }).IsTerminal.ShouldBeFalse();
    }

    [Fact]
    public void An_unknown_kind_projects_to_a_terminal_stop()
    {
        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision { Kind = "wat" });

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Stop);
        decision.IsTerminal.ShouldBeTrue("an unrecognized verb fails closed to a terminal stop");
    }

    [Fact]
    public void Projection_is_deterministic_in_the_model_decision()
    {
        var model = new SupervisorModelDecision { Kind = SupervisorDecisionKinds.Spawn, Spawn = new SupervisorSpawnPayload { SubtaskIds = new[] { "a" } } };

        SupervisorDecisionProjector.Project(model).PayloadJson.ShouldBe(SupervisorDecisionProjector.Project(model).PayloadJson, "same model decision → byte-identical canonical payload (the idempotency-key stability the ledger relies on)");
    }

    private static SupervisorDecision Project(string kind, Func<SupervisorModelDecision, SupervisorModelDecision> fill) =>
        SupervisorDecisionProjector.Project(fill(new SupervisorModelDecision { Kind = kind }));

    private static LlmSupervisorDecider Decider(SupervisorModelDecision model) =>
        new(new FakeRegistry(new FakeStructuredClient(model)));

    // ── Fakes at the honest IStructuredLLMClient seam ────────────────────────────────

    private sealed class FakeRegistry : ILLMClientRegistry
    {
        public FakeRegistry(IStructuredLLMClient? structured) =>
            All = structured == null ? Array.Empty<ILLMClient>() : new ILLMClient[] { (ILLMClient)structured };

        public IReadOnlyList<ILLMClient> All { get; }

        public ILLMClient Resolve(string provider) => All.First();
    }

    private sealed class FakeStructuredClient : ILLMClient, IStructuredLLMClient
    {
        private readonly SupervisorModelDecision _model;

        public FakeStructuredClient(SupervisorModelDecision model) => _model = model;

        public string Provider => "TestSupervisor";

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LLMCompletion { Text = "", Model = request.Model });

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new StructuredLLMCompletion { Json = JsonSerializer.SerializeToElement(_model, SupervisorDecisionSchema.Options), Model = request.Model });
    }
}
